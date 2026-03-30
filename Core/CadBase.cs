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

namespace process_pipeline.Core
{
   public class ProgressContext
    {
        private readonly ProgressMeter _pm;
        private readonly CancellationTokenSource _cts;
        private int _currentProgress = 0;

        public CancellationToken Token => _cts.Token;

        public ProgressContext(ProgressMeter pm, CancellationTokenSource cts)
        {
            _pm = pm;
            _cts = cts;
        }

        public void SetTotal(int total) { 
            _pm.SetLimit(total);
            System.Windows.Forms.Application.DoEvents();
        } 

        public void Step()
        {
            _pm.MeterProgress();
            _currentProgress++;

            // 统一在这里检测 ESC 键
            if ((WINAPI.GetAsyncKeyState(WINAPI.VK_ESCAPE) & 0x8000) != 0)
            {
                _cts.Cancel();
            }

            _cts.Token.ThrowIfCancellationRequested();

            // 统一的 UI 喘息机制
            if (_currentProgress % 10 == 0)
            {
                System.Windows.Forms.Application.DoEvents();
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
        /// 简化的命令行输出（封装WriteMessage，统一格式）
        /// </summary>
        protected void WriteLog(string message)
        {
            Ed.WriteMessage($"\n[管线处理工具] {message}");
        }

        internal void Run(string taskName, bool bOnlyUpdate = true)
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
                // 1. 执行核心逻辑，拿到泛型结果
                TResult result = Execute(context);
                
                // 2. 统一处理取消
                if (context.Token.IsCancellationRequested)
                {
                    Ed.WriteMessage("\n*** 操作已由用户中途取消 ***\n");
                }
                else
                {
                    if (result != null)
                        // 3. 成功后，将结果交给子类处理（比如弹窗、写文件等）
                        OnSuccess(result, bOnlyUpdate);
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

        /// <summary>
        /// 【可选重写】任务成功完成后的回调（用于展示结果）
        /// </summary>
        protected virtual void OnSuccess(TResult result, bool bOnlyUpdate) { }
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

        /// <summary>
        /// 【必须实现】无返回值的核心业务逻辑,留给子类去实现这个无返回值的方法
        /// </summary>
        protected abstract void ExecuteVoid(ProgressContext context);

        // 封死带返回值的 OnSuccess，暴露无返回值的 OnSuccessVoid 给子类
        protected sealed override void OnSuccess(object result, bool bUpdate)
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
