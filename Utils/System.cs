using Autodesk.AutoCAD.EditorInput;
using process_pipeline.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Utils
{
    public class SysVarScope : IDisposable
    {
        private readonly Dictionary<string, object> _old = new Dictionary<string, object>();
        private bool _disposed;

        public SysVarScope(Dictionary<string, object> newValues)
        {
            foreach (var kv in newValues)
            {
                try
                {
                    _old[kv.Key] = AcadApp.GetSystemVariable(kv.Key);
                    AcadApp.SetSystemVariable(kv.Key, kv.Value);
                }
                catch
                {
                    // 某些CAD不支持该变量时忽略
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var kv in _old)
            {
                try
                {
                    AcadApp.SetSystemVariable(kv.Key, kv.Value);
                }
                catch
                {
                    // 恢复失败也不阻断
                }
            }
        }
    }

    public static class DbgLog
    {
        private const string Prefix = "\n[Debug] ";
        
        [Conditional("DEBUG")]
        public static void Write(Editor ed, string message)
        {
            ed.WriteMessage(Prefix + message);
        }

        [Conditional("DEBUG")]
        public static void Write(Editor ed, string format, params object[] args)
        {
            if (ed == null) return;
            ed.WriteMessage(Prefix + string.Format(format, args));
        }
    }
}
