using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using process_pipeline.Models;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;

namespace process_pipeline.Core
{
    /// <summary>
    /// 单文档核查会话容器（Commit 3.2）。
    /// 
    /// 架构设计说明：
    /// 1. 为什么需要按 Document / Database 隔离会话状态：
    ///    在 AutoCAD 多文档（MDI）环境下，每个 DWG 文档拥有独立的 Database 与 ObjectId 空间。
    ///    原先散落在 FlowArrowService.ArrowToPipes / PipeToArrows、ArrowCacheManager._arrowCache 中的
    ///    全局 static 字典会导致：
    ///    - 多张图纸同时打开时，不同图纸的 ObjectId 混杂在同一字典中造成拓扑串扰；
    ///    - 在两个 DWG 标签页之间来回切换时，箭头缓存被迫清空重建，且上一张图纸的人工核查状态丢失。
    /// 2. 本类将单张图纸的：
    ///    - 当前核查问题字典（CurrentIssues，含人工判读状态）
    ///    - 箭头空间缓存（ArrowCache / IsArrowCacheInitialized）
    ///    - 箭头与管线的双向拓扑映射（ArrowToPipes / PipeToArrows）
    ///    - 当前激活的核查规则实例（ActiveRule）
    ///    全部收拢到与 Database 一一绑定的会话实例中，随文档销毁自动释放。
    /// </summary>
    public class DocumentCheckSession
    {
        /// <summary>
        /// 当前会话绑定的 CAD 数据库实例。
        /// </summary>
        public AcadDb.Database Db { get; }

        /// <summary>
        /// 当前文档的核查问题字典（ObjectId -> CheckIssueItem），包含人工判读状态与备注。
        /// </summary>
        public Dictionary<ObjectId, CheckIssueItem> CurrentIssues { get; set; } =
            new Dictionary<ObjectId, CheckIssueItem>();

        /// <summary>
        /// 当前文档的流向箭头空间缓存（Arrow ObjectId -> ArrowCacheInfo）。
        /// </summary>
        public Dictionary<ObjectId, ArrowCacheInfo> ArrowCache { get; } =
            new Dictionary<ObjectId, ArrowCacheInfo>();

        /// <summary>
        /// 标记当前文档的流向箭头缓存是否已完成首次全量扫描。
        /// </summary>
        public bool IsArrowCacheInitialized { get; set; }

        /// <summary>
        /// 箭头 -> 管线集合正向映射（当箭头移动/旋转/删除时，极速查出当前图纸中哪些管线受影响）。
        /// </summary>
        public Dictionary<ObjectId, HashSet<ObjectId>> ArrowToPipes { get; } =
            new Dictionary<ObjectId, HashSet<ObjectId>>();

        /// <summary>
        /// 管线 -> 箭头集合反向映射（当管线重新核查时，用于解绑旧的箭头关联）。
        /// </summary>
        public Dictionary<ObjectId, HashSet<ObjectId>> PipeToArrows { get; } =
            new Dictionary<ObjectId, HashSet<ObjectId>>();

        /// <summary>
        /// 当前文档正在使用的核查规则（供增量刷新调度器直接复用）。
        /// </summary>
        public ICheckRule ActiveRule { get; set; }

        /// <summary>
        /// 当前文档专属的混合撤销/重做管理器（Commit 4.1）。
        /// </summary>
        public HybridUndoRedoManager UndoRedoManager { get; } = new HybridUndoRedoManager();

        public DocumentCheckSession(AcadDb.Database db)
        {
            Db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// 按核查规则 ID 合并更新当前文档会话的问题集合（Commit 5.1）。
        /// 
        /// 设计说明：
        /// 当核查员在同一张图纸上先后执行不同核查命令（如先执行流向箭头核查，后执行管段匹配核查）时：
        /// - 全量模式（isIncremental = false）：仅清理并替换属于当前 ruleId 的旧记录（同时保留人工已标记为“已忽略/已修复”的判读状态），
        ///   而不会冲掉其他核查规则（其他 RuleId）已经产出的结果；
        /// - 增量模式（isIncremental = true）：仅就地更新本次受影响的 ObjectId 记录。
        /// </summary>
        public Dictionary<ObjectId, CheckIssueItem> MergeRuleIssues(
            string ruleId,
            Dictionary<ObjectId, CheckIssueItem> ruleIssues,
            bool isIncremental = false)
        {
            if (CurrentIssues == null)
            {
                CurrentIssues = new Dictionary<ObjectId, CheckIssueItem>();
            }

            if (ruleIssues == null)
                return CurrentIssues;

            if (!isIncremental && !string.IsNullOrWhiteSpace(ruleId))
            {
                // 收集属于当前规则但需保留人工判读状态（Ignored / Resolved）的旧记录
                var existingSameRule = new Dictionary<ObjectId, CheckIssueItem>();
                var keysToRemove = new List<ObjectId>();
                foreach (var kvp in CurrentIssues)
                {
                    if (kvp.Value != null && string.Equals(kvp.Value.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
                    {
                        existingSameRule[kvp.Key] = kvp.Value;
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (ObjectId key in keysToRemove)
                {
                    CurrentIssues.Remove(key);
                }

                foreach (var kvp in ruleIssues)
                {
                    CheckIssueItem item = kvp.Value;
                    if (item == null) continue;

                    if (existingSameRule.TryGetValue(kvp.Key, out var oldItem) &&
                        oldItem != null &&
                        oldItem.Status == IssueReviewStatus.Ignored &&
                        item.Status == IssueReviewStatus.Pending)
                    {
                        item.Status = IssueReviewStatus.Ignored;
                        item.Remark = oldItem.Remark;
                    }

                    CurrentIssues[kvp.Key] = item;
                }
            }
            else
            {
                foreach (var kvp in ruleIssues)
                {
                    if (kvp.Value != null)
                    {
                        CurrentIssues[kvp.Key] = kvp.Value;
                    }
                }
            }

            return new Dictionary<ObjectId, CheckIssueItem>(CurrentIssues);
        }

        /// <summary>
        /// 仅清空箭头空间缓存并重置初始化标志。
        /// </summary>
        public void ClearArrowCache()
        {
            ArrowCache.Clear();
            IsArrowCacheInitialized = false;
        }

        /// <summary>
        /// 清空当前文档的所有核查状态、拓扑映射与空间缓存（在文档关闭时调用）。
        /// </summary>
        public void ClearAll()
        {
            CurrentIssues?.Clear();
            ArrowCache.Clear();
            IsArrowCacheInitialized = false;
            ArrowToPipes.Clear();
            PipeToArrows.Clear();
            ActiveRule = null;
            UndoRedoManager.Clear();
        }
    }

    /// <summary>
    /// 多文档核查会话管理器（Commit 3.2）。
    /// 按 <see cref="AcadDb.Database"/> 索引并管理所有打开图纸的 <see cref="DocumentCheckSession"/>，
    /// 并在图纸关闭（DocumentToBeDestroyed）时自动回收对应会话，防止内存泄漏。
    /// </summary>
    public static class DocumentSessionManager
    {
        private static readonly Dictionary<AcadDb.Database, DocumentCheckSession> _sessions =
            new Dictionary<AcadDb.Database, DocumentCheckSession>();

        static DocumentSessionManager()
        {
            try
            {
                if (AcadApp.DocumentManager != null)
                {
                    AcadApp.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
                }
            }
            catch
            {
                // 在非 CAD 宿主环境（如独立测试）中安全忽略
            }
        }

        private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            if (e?.Document?.Database != null)
            {
                RemoveSession(e.Document.Database);
            }
        }

        /// <summary>
        /// 获取或创建指定数据库对应的核查会话。
        /// </summary>
        public static DocumentCheckSession GetOrCreateSession(AcadDb.Database db)
        {
            if (db == null) return null;

            if (!_sessions.TryGetValue(db, out DocumentCheckSession session))
            {
                session = new DocumentCheckSession(db);
                _sessions[db] = session;
            }
            return session;
        }

        /// <summary>
        /// 获取当前活动 MDI 文档的核查会话（若无活动文档则返回 null）。
        /// </summary>
        public static DocumentCheckSession GetActiveSession()
        {
            var activeDoc = AcadApp.DocumentManager?.MdiActiveDocument;
            if (activeDoc?.Database == null) return null;
            return GetOrCreateSession(activeDoc.Database);
        }

        /// <summary>
        /// 移除并清理指定数据库的核查会话。
        /// </summary>
        public static void RemoveSession(AcadDb.Database db)
        {
            if (db == null) return;

            if (_sessions.TryGetValue(db, out DocumentCheckSession session))
            {
                session.ClearAll();
                _sessions.Remove(db);
            }
        }
    }
}

