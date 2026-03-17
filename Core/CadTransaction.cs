using Autodesk.AutoCAD.DatabaseServices;
using System;

namespace process_pipeline.Core
{
    /// <summary>
    /// 事务封装工具：简化事务操作
    /// </summary>
    public static class CadTransaction
    {
        /// <summary>
        /// 执行带事务的操作（自动开启/提交，异常自动回滚）
        /// </summary>
        public static void Execute(Action<Transaction, Database> action)
        {
            using (Transaction tr = HostApplicationServices.WorkingDatabase.TransactionManager.StartTransaction())
            {
                try
                {
                    action(tr, HostApplicationServices.WorkingDatabase);
                    tr.Commit();
                }
                catch (Exception ex)
                {
                    tr.Abort();
                    throw new Exception($"事务执行失败：{ex.Message}");
                }
            }
        }
    }
}
