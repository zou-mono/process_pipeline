using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using System.Runtime.InteropServices;
using System;
using System.Threading;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using process_pipeline.Utils;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using process_pipeline.Commands;
using process_pipeline.Forms;

namespace process_pipeline.Core
{
    public class ProgressContext : IDisposable
    {
        private const long UiPumpIntervalMs = 40;
        private const int MaxMeterTicks = 100;

        private readonly ProgressMeter _pm;
        private readonly CancellationTokenSource _cts;
        private readonly bool _ownsResources;
        private readonly bool _allowCancel;
        private readonly bool _isHeadless;
        private readonly System.Diagnostics.Stopwatch _throttleWatch;
        private long _lastUiPumpMs;
        private int _currentProgress = 0;
        private int _total = 100;
        private int _meterLimit = 100;
        private int _lastMeterTick = 0;
        private System.Windows.Input.Cursor _previousCursor;
        private bool _hasOverriddenCursor;
        private bool _disposed;

        public CancellationToken Token => _cts != null ? _cts.Token : CancellationToken.None;

        public bool IsCancelled => _cts != null && _cts.IsCancellationRequested;

        public ProgressContext(ProgressMeter pm, CancellationTokenSource cts, bool ownsResources = false, bool allowCancel = true)
        {
            _pm = pm;
            _cts = cts ?? new CancellationTokenSource();
            _ownsResources = ownsResources;
            _allowCancel = allowCancel;
            _isHeadless = Core.Config.ConfigManager.IsHeadlessOrSuppressedUi;
            _throttleWatch = System.Diagnostics.Stopwatch.StartNew();
            _lastUiPumpMs = 0;
        }

        /// <summary>
        /// 开启一个自动管理生命周期的 CAD 系统状态栏进度条与等待光标作用域（支持 using 自动释放）。
        /// 在无头测试环境（ConfigManager.IsHeadlessOrSuppressedUi = true）下自动降级为纯内存无 UI 进度上下文。
        /// </summary>
        public static ProgressContext StartScope(string taskTitle, bool allowCancel = true)
        {
            var cts = new CancellationTokenSource();
            bool isHeadless = Core.Config.ConfigManager.IsHeadlessOrSuppressedUi;
            ProgressMeter pm = null;
            System.Windows.Input.Cursor prevCursor = null;
            bool cursorOverridden = false;

            if (!isHeadless)
            {
                try
                {
                    prevCursor = System.Windows.Input.Mouse.OverrideCursor;
                    System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                    cursorOverridden = true;
                }
                catch
                {
                    cursorOverridden = false;
                }

                try
                {
                    pm = new ProgressMeter();
                    string label = allowCancel
                        ? $"{taskTitle}，按 [ESC] 键可中途取消..."
                        : $"{taskTitle}...";
                    pm.Start(label);
                    pm.SetLimit(MaxMeterTicks);
                    System.Windows.Forms.Application.DoEvents();
                }
                catch
                {
                    pm?.Dispose();
                    pm = null;
                }
            }

            var ctx = new ProgressContext(pm, cts, ownsResources: true, allowCancel: allowCancel)
            {
                _previousCursor = prevCursor,
                _hasOverriddenCursor = cursorOverridden
            };
            return ctx;
        }

        /// <summary>
        /// 切换多阶段进度条的当前阶段标题并重置当前阶段进度。
        /// </summary>
        public void SetStage(string stageTitle, int total = 0)
        {
            _currentProgress = 0;
            _lastMeterTick = 0;
            _total = Math.Max(1, total > 0 ? total : MaxMeterTicks);
            _meterLimit = Math.Min(_total, MaxMeterTicks);

            if (!_isHeadless && _pm != null)
            {
                try
                {
                    _pm.Stop();
                    string label = _allowCancel
                        ? $"{stageTitle}，按 [ESC] 键可中途取消..."
                        : $"{stageTitle}...";
                    _pm.Start(label);
                    _pm.SetLimit(_meterLimit);
                    System.Windows.Forms.Application.DoEvents();
                    _lastUiPumpMs = _throttleWatch.ElapsedMilliseconds;
                }
                catch
                {
                    // 忽略进度条阶段标题切换异常
                }
            }
        }

        public void SetTotal(int total)
        {
            _currentProgress = 0;
            _lastMeterTick = 0;
            _total = Math.Max(1, total);
            _meterLimit = Math.Min(_total, MaxMeterTicks);

            if (!_isHeadless && _pm != null)
            {
                try
                {
                    _pm.SetLimit(_meterLimit);
                    long nowMs = _throttleWatch.ElapsedMilliseconds;
                    if (nowMs - _lastUiPumpMs >= UiPumpIntervalMs)
                    {
                        _lastUiPumpMs = nowMs;
                        System.Windows.Forms.Application.DoEvents();
                    }
                }
                catch
                {
                    // 忽略进度条上限设置异常
                }
            }
        }

        public void Step()
        {
            _currentProgress++;

            if (!_isHeadless && _pm != null && _total > 0)
            {
                int targetTick = Math.Min(_meterLimit, (int)((long)_currentProgress * _meterLimit / _total));
                while (_lastMeterTick < targetTick)
                {
                    _pm.MeterProgress();
                    _lastMeterTick++;
                }
            }

            // 按时间片（40ms = 25FPS）节流执行 ESC 按键检测与 UI 消息泵喘息，杜绝大图纸数千次高频 DoEvents 拖慢核查速度
            if (!_isHeadless)
            {
                long nowMs = _throttleWatch.ElapsedMilliseconds;
                if (nowMs - _lastUiPumpMs >= UiPumpIntervalMs)
                {
                    _lastUiPumpMs = nowMs;

                    if (_allowCancel && (WINAPI.GetAsyncKeyState(WINAPI.VK_ESCAPE) & 0x8000) != 0)
                    {
                        _cts?.Cancel();
                    }

                    System.Windows.Forms.Application.DoEvents();
                }
            }

            _cts?.Token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_ownsResources)
            {
                try
                {
                    _pm?.Stop();
                    _pm?.Dispose();
                }
                catch
                {
                    // 忽略进度条停止异常
                }

                try
                {
                    _cts?.Dispose();
                }
                catch
                {
                    // 忽略 CTS 释放异常
                }

                if (_hasOverriddenCursor)
                {
                    try
                    {
                        System.Windows.Input.Mouse.OverrideCursor = _previousCursor;
                    }
                    catch
                    {
                        // 忽略光标恢复异常
                    }
                    _hasOverriddenCursor = false;
                }
            }
        }
    }

    // 提取一个静态 API 类专门放底层方法，避免泛型类里放 DllImport 引起警告
    internal static class WINAPI
    {
        [DllImport("user32.dll")]
        public static extern bool EnableWindow(IntPtr hWnd, bool bEnable);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
        public const int VK_ESCAPE = 0x1B;
    }


    /// <summary>
    /// CAD基础类：封装通用的Document/Editor/Database，所有命令类继承
    /// </summary>
    public abstract class CadBase<TResult>
    {
        // 通用CAD上下文（所有命令都需要）
        protected Document Doc { get; private set; }
        protected Editor Ed { get; private set; }
        protected AcadDb.Database Db { get; private set; }

        public string taskName { get; private set; }

        protected  CadBase(AcadDb.Database db, Editor ed)
        {
            // 初始化CAD上下文（避免每个命令重复写这几行）
            Doc = Application.DocumentManager.MdiActiveDocument;
            Ed = ed;
            Db = db;
        }

        /// <summary>
        /// 【新增】判断结果是否有效。默认不为 null 则有效。
        /// </summary>
        protected virtual bool IsResultValid(TResult result)
        {
            return result != null;
        }

        /// <summary>
        /// 简化的命令行输出（封装WriteMessage，统一格式）
        /// </summary>
        protected void WriteLog(string message)
        {
            Ed.WriteMessage($"\n[管线处理工具] {message}");
        }

        internal void Run(string taskName, bool bOnlyUpdate = true, List<ObjectId> objectIds = null)
        {
            this.taskName = taskName;

            IntPtr cadHandle = AcadApp.MainWindow.Handle;

            // 禁用 CAD 窗口
            WINAPI.EnableWindow(cadHandle, false);

            ProgressMeter pm = new ProgressMeter();
            pm.Start($"{taskName}，按 [ESC] 键可中途取消...");

            // 逼迫 CAD 立刻重绘界面，把进度条显示出来！
            System.Windows.Forms.Application.DoEvents();

            CancellationTokenSource cts = new CancellationTokenSource();
            ProgressContext context = new ProgressContext(pm, cts);

            try
            {                
                TResult result;
                // 1. 执行核心逻辑，拿到泛型结果
                if (objectIds != null)
                {
                    result = Execute(context, objectIds);
                }
                else 
                { 
                    result = Execute(context);
                }
                
                // 2. 统一处理取消
                if (context.Token.IsCancellationRequested)
                {
                    Ed.WriteMessage("\n*** 操作已由用户中途取消 ***\n");
                }
                else
                {
                    // 【修改这里】：使用 IsResultValid 而不是直接判断 null
                    if (IsResultValid(result))
                    {
                        OnSuccess(result, bOnlyUpdate);
                    }
                }
            }
            catch (OperationCanceledException) 
            {
                // 【新增这个 catch】专门接住 Step() 抛出的取消异常
                Ed.WriteMessage("\n*** 操作已由用户中途取消 ***\n");
            }
            catch (System.Exception ex)
            {
                Ed.WriteMessage($"\n执行 [{taskName}] 时发生错误: {ex.Message}");
            }
            finally
            {
                cts.Dispose();
                pm.Stop();
                pm.Dispose();

                // 恢复 CAD 窗口
                WINAPI.EnableWindow(cadHandle, true);
                AcadApp.MainWindow.Focus();
            }
        }

        /// <summary>
        /// 【抽象方法】子类必须实现的核心业务逻辑
        /// </summary>
        protected abstract TResult Execute(ProgressContext context);

        protected abstract TResult Execute(ProgressContext context, List<ObjectId> objectIds);

        /// <summary>
        /// 【可选重写】任务成功完成后的回调（用于展示结果）
        /// </summary>
        protected virtual void OnSuccess(TResult result, bool bOnlyUpdate) { }

        protected virtual void OnFail() { }
    }

    /// <summary>
    /// 【核心】无返回值的 CAD 任务基类（适用于纯修改图纸、不需要弹窗展示结果的命令）
    /// </summary>
    public abstract class CadBase : CadBase<object>
    {
        protected CadBase(AcadDb.Database db, Editor ed) : base(db, ed) { }

        // 封死带返回值的 ExecuteCore，暴露无返回值的 ExecuteVoid 给子类
        protected sealed override object Execute(ProgressContext context)
        {
            ExecuteVoid(context);
            return null; 
        }

        protected sealed override object Execute(ProgressContext context, List<ObjectId> objectIds)
        {
            ExecuteVoid(context, objectIds);
            return null; 
        }

        // 【关键重写】：对于无返回值的任务，永远返回 true，跳过 ShowErrorDialog
        protected override bool IsResultValid(object result)
        {
            return true; 
        }

        /// <summary>
        /// 【必须实现】无返回值的核心业务逻辑,留给子类去实现这个无返回值的方法
        /// </summary>
        protected abstract void ExecuteVoid(ProgressContext context);

        protected abstract void ExecuteVoid(ProgressContext context, List<ObjectId> objectIds);

        // 封死带返回值的 OnSuccess，暴露无返回值的 OnSuccessVoid 给子类
        protected sealed override void OnSuccess(object result, bool bOnlyUpdate)
        {
            OnSuccessVoid();
        }

        /// <summary>
        /// 【可选重写】任务成功完成后的回调
        /// </summary>
        protected virtual void OnSuccessVoid() 
        {
            Ed.WriteMessage("\n操作执行完毕。\n");
        }
    }
}
