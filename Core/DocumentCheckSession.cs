using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using process_pipeline.Core.DataSource;
using process_pipeline.Models;
using process_pipeline.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;

namespace process_pipeline.Core
{
    /// <summary>
    /// 单文档核查会话容器（Commit 3.2 & Stage 2 Commit 2.3）。
    /// 
    /// 架构设计说明：
    /// 1. 为什么需要按 Document / Database 隔离会话状态：
    ///    在 AutoCAD 多文档（MDI）环境下，每个 DWG 文档拥有独立的 Database 与 ObjectId 空间。
    ///    原先散落在 FlowArrowService.ArrowToPipes / PipeToArrows、ArrowCacheManager._arrowCache 中的
    ///    全局 static 字典会导致：
    ///    - 多张图纸同时打开时，不同图纸的 ObjectId 混杂在同一字典中造成拓扑串扰；
    ///    - 在两个 DWG 标签页之间来回切换时，箭头缓存被迫清空重建，且上一张图纸的人工核查状态丢失。
    /// 2. Stage 2 Commit 2.3 架构增强：
    ///    - 引入 <see cref="IssuesById"/>（以 IssueId "{RuleId}:{TargetHandle}:{SubIndex}" 为主键）与
    ///      <see cref="ObjectIdToIssueIds"/> 二级索引，彻底解决同一根管线在多核查规则（流向箭头、规划自检、管段匹配、折线段对比）
    ///      或多子折线段下因单一 ObjectId 字典键冲突导致的互相覆盖问题，同时支持 ObjectId.Null 的“上版缺失管段”问题存储；
    ///    - 引入 <see cref="ActiveRules"/> 多规则集合，支持单次会话同时挂载并增量监听多个核查规则；
    ///    - 引入 <see cref="DatasetContext"/>（<see cref="PipeDatasetContext"/>），统一承载规划富化属性、可编辑上版数据动态缓存与只读勘测数据静态缓存。
    /// </summary>
    public class DocumentCheckSession
    {
        private ICheckRule _activeRule;

        /// <summary>
        /// 当前会话绑定的 CAD 数据库实例。
        /// </summary>
        public AcadDb.Database Db { get; }

        /// <summary>
        /// 当前文档的核查问题字典（ObjectId -> CheckIssueItem），保留用于向后兼容单规则按 ObjectId 索引的调用方。
        /// </summary>
        public Dictionary<ObjectId, CheckIssueItem> CurrentIssues { get; set; } =
            new Dictionary<ObjectId, CheckIssueItem>();

        /// <summary>
        /// 按全局唯一 IssueId（格式："{RuleId}:{TargetHandle}:{SubIndex}"）索引的主问题字典（Stage 2 Commit 2.3）。
        /// 支持同一管线挂载多条不同规则/不同折线段的问题，以及 TargetObjectId == ObjectId.Null 的缺失管段问题。
        /// </summary>
        public Dictionary<string, CheckIssueItem> IssuesById { get; } =
            new Dictionary<string, CheckIssueItem>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// CAD 实体 ObjectId -> 关联的 IssueId 列表二级索引（Stage 2 Commit 2.3），
        /// 供图面点选联动与局部增量刷新 O(1) 反查该管线上的全部核查问题。
        /// </summary>
        public Dictionary<ObjectId, List<string>> ObjectIdToIssueIds { get; } =
            new Dictionary<ObjectId, List<string>>();

        /// <summary>
        /// 当前文档的三方管线数据集上下文（规划富化属性 + 可编辑上版数据动态缓存 + 只读勘测数据静态缓存）。
        /// </summary>
        public PipeDatasetContext DatasetContext { get; } = new PipeDatasetContext();

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
        /// 当前文档已注册激活的核查规则集合（RuleId -> ICheckRule，Stage 2 Commit 2.3）。
        /// </summary>
        public Dictionary<string, ICheckRule> ActiveRules { get; } =
            new Dictionary<string, ICheckRule>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 当前文档正在使用的最近主核查规则（兼容旧调用方；设置时自动注册进 <see cref="ActiveRules"/>）。
        /// </summary>
        public ICheckRule ActiveRule
        {
            get => _activeRule;
            set
            {
                _activeRule = value;
                if (value != null && !string.IsNullOrWhiteSpace(value.RuleId))
                {
                    ActiveRules[value.RuleId] = value;
                }
            }
        }

        /// <summary>
        /// 注册并激活一个核查规则实例。
        /// </summary>
        public void RegisterActiveRule(ICheckRule rule)
        {
            if (rule == null) return;
            ActiveRule = rule;
        }

        /// <summary>
        /// 当前文档专属的混合撤销/重做管理器（Commit 4.1）。
        /// </summary>
        public HybridUndoRedoManager UndoRedoManager { get; } = new HybridUndoRedoManager();

        public DocumentCheckSession(AcadDb.Database db)
        {
            Db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// 确保核查问题项具备合法的唯一 IssueId。
        /// </summary>
        public static string EnsureIssueId(CheckIssueItem item, string fallbackRuleId = null, int subIndex = 0)
        {
            if (item == null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(item.IssueId))
                return item.IssueId;

            string rId = !string.IsNullOrWhiteSpace(item.RuleId)
                ? item.RuleId
                : (!string.IsNullOrWhiteSpace(fallbackRuleId) ? fallbackRuleId : "General");

            string handle = !string.IsNullOrWhiteSpace(item.TargetHandle)
                ? item.TargetHandle
                : (!item.TargetObjectId.IsNull && item.TargetObjectId.IsValid
                    ? item.TargetObjectId.Handle.ToString()
                    : "NULL");

            item.IssueId = $"{rId}:{handle}:{subIndex}";
            return item.IssueId;
        }

        /// <summary>
        /// 按核查规则 ID 合并更新当前文档会话的问题集合（支持同一 ObjectId 多条问题及 ObjectId.Null 缺失问题，Stage 2 Commit 2.3）。
        /// </summary>
        public IReadOnlyList<CheckIssueItem> MergeRuleIssueItems(
            string ruleId,
            IEnumerable<CheckIssueItem> ruleIssues,
            bool isIncremental = false,
            IEnumerable<ObjectId> incrementalTargetIds = null)
        {
            if (CurrentIssues == null)
            {
                CurrentIssues = new Dictionary<ObjectId, CheckIssueItem>();
            }

            if (ruleIssues == null)
                return GetAllIssueItems();

            var incomingList = ruleIssues.Where(x => x != null).ToList();

            if (!isIncremental && !string.IsNullOrWhiteSpace(ruleId))
            {
                // 1. 收集属于当前规则的旧记录（用于保留 Ignored 状态与人工备注）
                var existingSameRuleById = new Dictionary<string, CheckIssueItem>(StringComparer.OrdinalIgnoreCase);
                var issueIdsToRemove = new List<string>();

                foreach (var kvp in IssuesById)
                {
                    if (kvp.Value != null && string.Equals(kvp.Value.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
                    {
                        existingSameRuleById[kvp.Key] = kvp.Value;
                        issueIdsToRemove.Add(kvp.Key);
                    }
                }

                foreach (string idToRemove in issueIdsToRemove)
                {
                    RemoveIssueByIdInternal(idToRemove);
                }

                var currentKeysToRemove = CurrentIssues
                    .Where(kvp => kvp.Value != null && string.Equals(kvp.Value.RuleId, ruleId, StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (ObjectId oid in currentKeysToRemove)
                {
                    CurrentIssues.Remove(oid);
                }

                // 2. 写入当前规则的新问题并继承人工忽略状态
                int seq = 0;
                foreach (CheckIssueItem item in incomingList)
                {
                    if (string.IsNullOrWhiteSpace(item.RuleId) && !string.IsNullOrWhiteSpace(ruleId))
                    {
                        item.RuleId = ruleId;
                    }

                    string issueId = EnsureIssueId(item, ruleId, seq++);
                    if (existingSameRuleById.TryGetValue(issueId, out var oldItem) &&
                        oldItem != null &&
                        oldItem.Status == IssueReviewStatus.Ignored &&
                        item.Status == IssueReviewStatus.Pending)
                    {
                        item.Status = IssueReviewStatus.Ignored;
                        item.Remark = oldItem.Remark;
                    }

                    UpsertIssueInternal(item);
                }
            }
            else
            {
                // 增量模式：若指定了受影响的管线 ObjectId 集合，可先清理这些 ObjectId 在本规则下的旧子问题（保留 Resolved/Ignored 或被新结果替换）
                int seq = 0;
                foreach (CheckIssueItem item in incomingList)
                {
                    if (string.IsNullOrWhiteSpace(item.RuleId) && !string.IsNullOrWhiteSpace(ruleId))
                    {
                        item.RuleId = ruleId;
                    }
                    EnsureIssueId(item, ruleId, seq++);
                    UpsertIssueInternal(item);
                }
            }

            return GetAllIssueItems();
        }

        /// <summary>
        /// 按核查规则 ID 合并更新当前文档会话的问题字典（兼容以 ObjectId 为键的旧调用方，同时同步更新 IssuesById 与二级索引）。
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

            foreach (var kvp in ruleIssues)
            {
                if (kvp.Value != null && kvp.Value.TargetObjectId.IsNull && !kvp.Key.IsNull)
                {
                    kvp.Value.TargetObjectId = kvp.Key;
                }
            }

            MergeRuleIssueItems(ruleId, ruleIssues.Values, isIncremental, ruleIssues.Keys);

            // 确保按 ObjectId 映射的字典对当前规则的键值完全同步
            foreach (var kvp in ruleIssues)
            {
                if (kvp.Value != null)
                {
                    CurrentIssues[kvp.Key] = kvp.Value;
                }
            }

            return new Dictionary<ObjectId, CheckIssueItem>(CurrentIssues);
        }

        /// <summary>
        /// 根据 CAD 实体 ObjectId 查询其挂载的所有核查问题（支持单管线多规则/多折线段问题）。
        /// </summary>
        public IReadOnlyList<CheckIssueItem> GetIssuesByObjectId(ObjectId targetId)
        {
            if (targetId.IsNull)
                return Array.Empty<CheckIssueItem>();

            var result = new List<CheckIssueItem>();
            if (ObjectIdToIssueIds.TryGetValue(targetId, out List<string> issueIds) && issueIds != null)
            {
                foreach (string id in issueIds)
                {
                    if (IssuesById.TryGetValue(id, out CheckIssueItem item) && item != null)
                    {
                        result.Add(item);
                    }
                }
            }

            if (result.Count == 0 && CurrentIssues != null && CurrentIssues.TryGetValue(targetId, out CheckIssueItem fallbackItem) && fallbackItem != null)
            {
                result.Add(fallbackItem);
            }

            return result;
        }

        /// <summary>
        /// 获取当前会话中的全部核查问题列表（汇总 <see cref="IssuesById"/> 与直接写入 <see cref="CurrentIssues"/> 的问题）。
        /// </summary>
        public IReadOnlyList<CheckIssueItem> GetAllIssueItems()
        {
            if (CurrentIssues != null)
            {
                int seq = 0;
                foreach (var kvp in CurrentIssues)
                {
                    if (kvp.Value == null) continue;
                    if (kvp.Value.TargetObjectId.IsNull && !kvp.Key.IsNull)
                    {
                        kvp.Value.TargetObjectId = kvp.Key;
                    }
                    string issueId = EnsureIssueId(kvp.Value, kvp.Value.RuleId, seq++);
                    if (!IssuesById.ContainsKey(issueId))
                    {
                        UpsertIssueInternal(kvp.Value);
                    }
                }
            }

            return IssuesById.Values.ToList();
        }

        private void UpsertIssueInternal(CheckIssueItem item)
        {
            if (item == null) return;

            string issueId = EnsureIssueId(item);
            IssuesById[issueId] = item;

            if (!item.TargetObjectId.IsNull)
            {
                if (!ObjectIdToIssueIds.TryGetValue(item.TargetObjectId, out List<string> idList))
                {
                    idList = new List<string>();
                    ObjectIdToIssueIds[item.TargetObjectId] = idList;
                }

                if (!idList.Contains(issueId, StringComparer.OrdinalIgnoreCase))
                {
                    idList.Add(issueId);
                }

                CurrentIssues[item.TargetObjectId] = item;
            }
        }

        private void RemoveIssueByIdInternal(string issueId)
        {
            if (string.IsNullOrWhiteSpace(issueId))
                return;

            if (IssuesById.TryGetValue(issueId, out CheckIssueItem removed) && removed != null)
            {
                IssuesById.Remove(issueId);
                if (!removed.TargetObjectId.IsNull &&
                    ObjectIdToIssueIds.TryGetValue(removed.TargetObjectId, out List<string> idList) &&
                    idList != null)
                {
                    idList.RemoveAll(x => string.Equals(x, issueId, StringComparison.OrdinalIgnoreCase));
                    if (idList.Count == 0)
                    {
                        ObjectIdToIssueIds.Remove(removed.TargetObjectId);
                    }
                }
            }
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
            IssuesById.Clear();
            ObjectIdToIssueIds.Clear();
            DatasetContext.ClearSessionData();
            ArrowCache.Clear();
            IsArrowCacheInitialized = false;
            ArrowToPipes.Clear();
            PipeToArrows.Clear();
            ActiveRules.Clear();
            _activeRule = null;
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

