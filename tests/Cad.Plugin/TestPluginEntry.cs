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
            EnsureDebuggerLogTarget();
        }

        public void Terminate()
        {
            LogManager.Flush();
        }

        /// <summary>
        /// 为 NLog 补充 DebuggerTarget（走 .NET 托管调试通道 OutputDebugString），
        /// 确保在 VS Code / Visual Studio 的“调试控制台 (Debug Console)”中输出纯正 UTF-8/Unicode 中文而不受 accoreconsole 标准输出管道编码影响。
        /// </summary>
        private static void EnsureDebuggerLogTarget()
        {
            try
            {
                LoggingConfiguration config = LogManager.Configuration ?? new LoggingConfiguration();
                if (config.FindTargetByName("vscode_debugger") == null)
                {
                    var debugTarget = new DebuggerTarget("vscode_debugger")
                    {
                        Layout = "[${level:uppercase=true}] ${logger:shortName=true}: ${message} ${exception:format=tostring}"
                    };
                    config.AddTarget(debugTarget);
                    config.AddRule(LogLevel.Debug, LogLevel.Fatal, debugTarget);
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
            EnsureDebuggerLogTarget();

            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc?.Editor;

            string targetCmd = Environment.GetEnvironmentVariable(EnvTestCommandKey);
            if (string.IsNullOrWhiteSpace(targetCmd))
            {
                targetCmd = DefaultTestCommand;
                WriteLog(ed, $"[run_test] 未检测到环境变量 {EnvTestCommandKey}，使用默认测试命令: {targetCmd}");
            }
            else
            {
                targetCmd = targetCmd.Trim();
                WriteLog(ed, $"[run_test] 从环境变量 {EnvTestCommandKey} 读取到目标测试命令: {targetCmd}");
            }

            var commandMap = DiscoverTestCommands();

            if (string.Equals(targetCmd, "list", StringComparison.OrdinalIgnoreCase))
            {
                PrintAvailableCommands(ed, commandMap);
                return;
            }

            if (!commandMap.TryGetValue(targetCmd, out MethodInfo targetMethod))
            {
                WriteLog(ed, $"[run_test] 错误：未找到名为 '{targetCmd}' 的测试命令！");
                PrintAvailableCommands(ed, commandMap);
                return;
            }

            WriteLog(ed, $"========== 开始执行测试命令: {targetCmd} ({targetMethod.DeclaringType?.FullName}.{targetMethod.Name}) ==========");
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                object instance = targetMethod.IsStatic
                    ? null
                    : Activator.CreateInstance(targetMethod.DeclaringType);

                targetMethod.Invoke(instance, null);

                sw.Stop();
                WriteLog(ed, $"========== 测试命令 {targetCmd} 执行完成，耗时: {sw.ElapsedMilliseconds} ms ==========");
            }
            catch (TargetInvocationException tie)
            {
                sw.Stop();
                System.Exception inner = tie.InnerException ?? tie;
                WriteLog(ed, $"========== 测试命令 {targetCmd} 执行异常 ({sw.ElapsedMilliseconds} ms) ==========");
                WriteLog(ed, $"{inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}");
                Logger.Error(inner, $"执行测试命令 {targetCmd} 异常");
            }
            catch (System.Exception ex)
            {
                sw.Stop();
                WriteLog(ed, $"========== 测试命令 {targetCmd} 执行异常 ({sw.ElapsedMilliseconds} ms) ==========");
                WriteLog(ed, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                Logger.Error(ex, $"执行测试命令 {targetCmd} 异常");
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

        private static void PrintAvailableCommands(Editor ed, Dictionary<string, MethodInfo> commandMap)
        {
            WriteLog(ed, "当前 Cad.Plugin 可用的测试命令列表：");
            int idx = 1;
            foreach (var kvp in commandMap.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                WriteLog(ed, $"  [{idx++}] {kvp.Key,-24} -> {kvp.Value.DeclaringType?.Name}.{kvp.Value.Name}()");
            }
        }

        private static void WriteLog(Editor ed, string message)
        {
            ed?.WriteMessage(message + "\n");
            Logger.Info(message);
        }
    }
}
