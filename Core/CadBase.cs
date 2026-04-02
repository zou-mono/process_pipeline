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

        protected abstract TResult Execute(ProgressContext context, List<ObjectId> objectIds);

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

        protected sealed override object Execute(ProgressContext context, List<ObjectId> objectIds)
        {
            ExecuteVoid(context, objectIds);
            return null; 
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

    public static class CadConfig
    {
        // 分别存储管线和箭头图层
        public static HashSet<string> PipeLayers { get; private set; }
        public static HashSet<string> ArrowLayers { get; private set; }
    
        // 合并集合：专供 PaletteRefreshManager 极速判断使用（只要是这俩里面的都拦截）
        //public static HashSet<string> AllTargetLayers { get; private set; }

        // 【新增】全局参数，并赋予默认值（防止配置文件里没写或者写错了）
        public static double MaxBufferDistance { get; private set; } = 50.0; 
        public static double AngleTolerance { get; private set; } = 30.0;

        private static readonly string[] DefaultPipeLayers = new string[]
        {
            "3-污水管-2025新建",
            "3-污水管-规划扩建",
            "3-污水管-现状",
            "3-污水压力管-规划新建",
            "3-污水压力管-现状"
        };

        private static readonly string[] DefaultArrowLayers = new string[]
        {
            "3-标注-流向",
            "污水-流向"
        };

        // 静态构造函数：在类第一次被使用时自动加载
        static CadConfig()
        {
            PipeLayers = new HashSet<string>();
            ArrowLayers = new HashSet<string>();
            //AllTargetLayers = new HashSet<string>();
        
            LoadConfigFromFile();
        }

        private static void LoadConfigFromFile()
        {
            // 状态追踪：记录是否找到了正确的节标签
            bool foundPipeSection = false;
            bool foundArrowSection = false;
            bool fileExisted = false; // 记录文件原本是否存在
            bool readError = false;   // 记录读取过程中是否发生异常

            string dllFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string configFilePath = Path.Combine(dllFolder, "Config.ini");

            try
            {
                if (File.Exists(configFilePath))
                {
                    fileExisted = true; // 标记文件存在    

                    string[] lines = File.ReadAllLines(configFilePath);
                    string currentSection = "";

                    foreach (string rawLine in lines)
                    {
                        string line = rawLine.Trim();
                    
                        // 忽略空行和注释
                        if (string.IsNullOrEmpty(line) || line.StartsWith("//")) continue;

                        // 严格匹配我们预期的节标题
                        if (line == "[Settings]") { currentSection = line; continue; }
                        if (line == "[PipeLayers]") { currentSection = line; foundPipeSection = true; continue; }
                        if (line == "[ArrowLayers]") { currentSection = line; foundArrowSection = true; continue; }

                        // 如果用户写了类似 [P1ipeLayers] 的错误标签，将其视为未知节，后续行会被忽略
                        if (line.StartsWith("[") && line.EndsWith("]"))
                        {
                            currentSection = "Unknown"; 
                            continue;
                        }

                        // 【新增】解析 [Settings] 节下的 Key=Value
                        if (currentSection == "[Settings]")
                        {
                            // 按照等号分割，最多分两部分
                            string[] parts = line.Split(new char[] { '=' }, 2);
                            if (parts.Length == 2)
                            {
                                string key = parts[0].Trim();
                                string value = parts[1].Trim();

                                // 尝试转换为 double，如果转换成功则赋值
                                if (key == "MaxBufferDistance" && double.TryParse(value, out double maxDist))
                                {
                                    MaxBufferDistance = maxDist;
                                }
                                else if (key == "AngleTolerance" && double.TryParse(value, out double angleTol))
                                {
                                    AngleTolerance = angleTol;
                                }
                            }
                        }

                        // 根据当前所在的节，将图层名加入不同的集合
                        if (currentSection == "[PipeLayers]")
                        {
                            PipeLayers.Add(line);
                            //AllTargetLayers.Add(line);
                        }
                        else if (currentSection == "[ArrowLayers]")
                        {
                            ArrowLayers.Add(line);
                            //AllTargetLayers.Add(line);
                        }
                    }
                }
                else
                {
                    // 文件不存在时，生成模板文件
                    CreateDefaultConfigFile(configFilePath);
                
                    // 重新调用自己，读取刚刚生成的文件
                    LoadConfigFromFile();
                }
            }
            catch
            {
                // 容错：如果读取失败，确保集合不为 null，避免引发空引用异常
                readError = true; // 捕获到读取异常（如文件被其他程序占用）
            }

            // ==========================================
            // 自愈机制与弹窗警告逻辑
            // ==========================================
            bool needWarning = false; // 是否需要弹出警告

            // 如果没找到 [PipeLayers] 标签（被改错了），或者标签下没有任何图层
            if (!foundPipeSection || PipeLayers.Count == 0)
            {
                needWarning = true;
                foreach (string layer in DefaultPipeLayers)
                {
                    PipeLayers.Add(layer);
                    //AllTargetLayers.Add(layer);
                }
            }

            // 如果没找到 [ArrowLayers] 标签，或者标签下没有任何图层
            if (!foundArrowSection || ArrowLayers.Count == 0)
            {
                needWarning = true;
                foreach (string layer in DefaultArrowLayers)
                {
                    ArrowLayers.Add(layer);
                    //AllTargetLayers.Add(layer);
                }
            }

            // ==========================================
            // 核心：发现错误 -> 备份原文件 -> 覆盖生成新模板 -> 弹窗提示
            // ==========================================

            // 只有当“文件原本存在” 且 “标签丢失/内容为空/读取报错” 时，才弹窗警告！
            if (fileExisted && (needWarning || readError))
            {
                try
                {
                    // 1. 生成备份文件名 (例如: Config_error_bak.ini)
                    string backupFilePath = Path.Combine(dllFolder, "Config_error_bak.ini");

                    // 2. 备份损坏的文件 (允许覆盖旧的备份)
                    if (File.Exists(configFilePath))
                    {
                        File.Copy(configFilePath, backupFilePath, true);
                    }

                    // 3. 重新生成标准的模板文件，覆盖掉损坏的 Config.ini
                    CreateDefaultConfigFile(configFilePath);

                    // 使用 CAD 原生的弹窗，确保模态显示在 CAD 窗口上
                    Application.ShowAlertDialog(
                        "【图层配置错误自动修复】\n\n" +
                        "检测到您的 Config.ini 配置文件格式错误或缺少必要标签。\n" +
                        "为了保证插件正常运行，系统已自动为您重置为标准默认配置。\n\n" +
                        "💡 您原来的配置文件已安全备份为：\n" +
                        "Config_error_bak.ini\n\n" +
                        "请打开新的 Config.ini 参考标准格式，并将您的自定义参数重新填入。"
                    );
                }
                catch
                {
                    // 忽略弹窗本身可能引发的异常（极少数情况下，如果 CAD 尚未完全初始化完毕时调用可能会报错）
                }
            }
        }

        private static void CreateDefaultConfigFile(string filePath)
        {
            List<string> content = new List<string>
            {
                "// CAD 插件全局配置文件",
                "// 注意：Settings 节使用 等号(=) 赋值，图层节每行填写一个图层名",
                "",
                "[Settings]",
                $"MaxBufferDistance={MaxBufferDistance}",
                $"AngleTolerance={AngleTolerance}",
                "",
                "[PipeLayers]"
            };
        
            content.AddRange(DefaultPipeLayers);
        
            content.Add("");
            content.Add("[ArrowLayers]");
            content.AddRange(DefaultArrowLayers);

            File.WriteAllLines(filePath, content);
        }

        // 如果用户在不关 CAD 的情况下修改了 txt，可以调用这个方法重新读取
        public static void Reload()
        {
            PipeLayers.Clear();
            ArrowLayers.Clear();

            // 恢复默认参数，防止读取失败时保留了旧的错误值
            MaxBufferDistance = 50.0;
            AngleTolerance = 30.0;
            //AllTargetLayers.Clear();
            LoadConfigFromFile();
        }

        public static void EnsureLoaded()
        {
            // 这是一个空方法。
            // 但是，当外部调用这个方法时，C# 机制会强制先执行 static CadConfig() 静态构造函数。
            // 从而立刻触发 LoadConfigFromFile() 的读取和检查逻辑。
        }
    }
}
