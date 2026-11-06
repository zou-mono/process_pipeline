using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Cad.Plugin
{
    public class TestPluginEntry : IExtensionApplication
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// 命令行/脚本传入目标测试命令名的环境变量名。
        /// </summary>
        public const string EnvTestCommandKey = "CAD_TEST_CMD";

        /// <summary>
        /// 当未设置环境变量（例如在 Visual Studio 中直接按 F5 调试）时默认执行的测试命令。
        /// </summary>
        public const string DefaultTestCommand = "test_match_pipe";

        public void Initialize()
        {
            LoggerSetup.Configure();
            EnsureTestLogTargets();
        }

        public void Terminate()
        {
            LogManager.Flush();
        }

        /// <summary>
        /// 为 NLog 补充测试专用输出目标：
        /// 1. DebuggerTarget（走 .NET 托管调试通道 OutputDebugString，供 VS Code / VS 调试控制台显示）；
        /// 2. CadEditorTarget（转发至 AutoCAD Editor.WriteMessage，解决 accoreconsole.exe 重定向 stdout 导致 NLog ConsoleTarget 在 cmd 中不可见的问题）。
        /// </summary>
        private static void EnsureTestLogTargets()
        {
            try
            {
                LoggingConfiguration config = LogManager.Configuration ?? new LoggingConfiguration();
                bool changed = false;

                if (config.FindTargetByName("vscode_debugger") == null)
                {
                    var debugTarget = new DebuggerTarget("vscode_debugger")
                    {
                        Layout = "[${level:uppercase=true}] ${logger:shortName=true}: ${message} ${exception:format=tostring}"
                    };
                    config.AddTarget(debugTarget);
                    config.AddRule(LogLevel.Debug, LogLevel.Fatal, debugTarget);
                    changed = true;
                }

                if (config.FindTargetByName("cad_editor") == null)
                {
                    var editorTarget = new CadEditorTarget("cad_editor")
                    {
                        Layout = "[${level:uppercase=true}] ${message} ${exception:format=tostring}"
                    };
                    config.AddTarget(editorTarget);
                    config.AddRule(LogLevel.Info, LogLevel.Fatal, editorTarget);
                    changed = true;
                }

                if (changed)
                {
                    LogManager.Configuration = config;
                    LogManager.ReconfigExistingLoggers();
                }
            }
            catch
            {
                // 忽略日志目标附加异常
            }
        }

        /// <summary>
        /// 统一测试路由入口命令（固定写在 load.scr 中，避免每次切换测试都修改 load.scr）。
        /// 优先读取环境变量 CAD_TEST_CMD；若未设置则回退执行 DefaultTestCommand。
        /// 通过反射自动发现当前测试程序集中标记了 [CommandMethod] 的测试方法并同步执行。
        /// </summary>
        [CommandMethod("run_test")]
        public void RunTest()
        {
            EnsureTestLogTargets();

            string targetCmd = Environment.GetEnvironmentVariable(EnvTestCommandKey);
            if (string.IsNullOrWhiteSpace(targetCmd))
            {
                targetCmd = DefaultTestCommand;
                Logger.Info($"[run_test] 未检测到环境变量 {EnvTestCommandKey}，使用默认测试命令: {targetCmd}");
            }
            else
            {
                targetCmd = targetCmd.Trim();
                Logger.Info($"[run_test] 从环境变量 {EnvTestCommandKey} 读取到目标测试命令: {targetCmd}");
            }

            var commandMap = DiscoverTestCommands();

            if (string.Equals(targetCmd, "list", StringComparison.OrdinalIgnoreCase))
            {
                PrintAvailableCommands(commandMap);
                return;
            }

            if (!commandMap.TryGetValue(targetCmd, out MethodInfo targetMethod))
            {
                Logger.Error($"[run_test] 错误：未找到名为 '{targetCmd}' 的测试命令！");
                PrintAvailableCommands(commandMap);
                return;
            }

            Logger.Info($"========== 开始执行测试命令: {targetCmd} ({targetMethod.DeclaringType?.FullName}.{targetMethod.Name}) ==========");
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                object instance = targetMethod.IsStatic
                    ? null
                    : Activator.CreateInstance(targetMethod.DeclaringType);

                targetMethod.Invoke(instance, null);

                sw.Stop();
                Logger.Info($"========== 测试命令 {targetCmd} 执行完成，耗时: {sw.ElapsedMilliseconds} ms ==========");
            }
            catch (TargetInvocationException tie)
            {
                sw.Stop();
                System.Exception inner = tie.InnerException ?? tie;
                Logger.Error(inner, $"========== 测试命令 {targetCmd} 执行异常 ({sw.ElapsedMilliseconds} ms) ==========");
            }
            catch (System.Exception ex)
            {
                sw.Stop();
                Logger.Error(ex, $"========== 测试命令 {targetCmd} 执行异常 ({sw.ElapsedMilliseconds} ms) ==========");
            }
        }

        /// <summary>
        /// 扫描当前测试插件程序集中所有标记了 [CommandMethod] 的无参测试方法。
        /// </summary>
        private static Dictionary<string, MethodInfo> DiscoverTestCommands()
        {
            var map = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
            Assembly asm = Assembly.GetExecutingAssembly();

            foreach (Type type in asm.GetTypes())
            {
                if (type.IsAbstract && !type.IsSealed)
                    continue;

                MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                foreach (MethodInfo method in methods)
                {
                    if (method.GetParameters().Length != 0)
                        continue;

                    var attrs = method.GetCustomAttributes(typeof(CommandMethodAttribute), inherit: true)
                        .OfType<CommandMethodAttribute>();

                    foreach (CommandMethodAttribute attr in attrs)
                    {
                        if (string.IsNullOrWhiteSpace(attr.GlobalName))
                            continue;

                        if (string.Equals(attr.GlobalName, "run_test", StringComparison.OrdinalIgnoreCase))
                            continue;

                        map[attr.GlobalName] = method;
                    }
                }
            }

            return map;
        }

        private static void PrintAvailableCommands(Dictionary<string, MethodInfo> commandMap)
        {
            Logger.Info("当前 Cad.Plugin 可用的测试命令列表：");
            int idx = 1;
            foreach (var kvp in commandMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                Logger.Info($"  [{idx++}] {kvp.Key,-24} -> {kvp.Value.DeclaringType?.Name}.{kvp.Value.Name}()");
            }
        }
    }

    /// <summary>
    /// 自定义 NLog 输出目标：将日志直接转发至 AutoCAD 当前活动文档的 Editor.WriteMessage，
    /// 使测试代码只需调用一次 Logger.Info / Warn / Error 即可同时在 cmd 终端、VS Code 调试控制台和本地日志文件中查看输出。
    /// </summary>
    public sealed class CadEditorTarget : TargetWithLayout
    {
        public CadEditorTarget(string name)
        {
            Name = name;
        }

        protected override void Write(LogEventInfo logEvent)
        {
            try
            {
                Editor ed = Application.DocumentManager?.MdiActiveDocument?.Editor;
                if (ed == null)
                    return;

                string text = RenderLogEvent(Layout, logEvent);
                if (!string.IsNullOrEmpty(text))
                {
                    // 注意：
                    // 1. 将消息内部的 \0（如 Newtonsoft.Json EOF 报错中嵌入的 '\0' 字符，会导致 C++ acutPrintf 提前截断并吞掉末尾 \n）和换行符清理掉；
                    // 2. 在 accoreconsole.exe 的真实 Win32 控制台（cmd.exe）中，Editor.WriteMessage 必须以末尾 "\n" 结束，
                    //    若写成前置 "\n" + text（末尾无 \n），accoreconsole 会认为当前行未结束并在下次调用时通过 SetConsoleCursorPosition 回到行首覆盖上一行！
                    text = text.Replace("\0", "<EOF>").Replace("\r", " ").Replace("\n", " ").TrimEnd();
                    ed.WriteMessage(text + "\n");
                }
            }
            catch
            {
                // 忽略 CAD 文档未就绪或切换时的输出异常
            }
        }
    }
}
