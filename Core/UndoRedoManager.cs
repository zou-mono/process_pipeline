using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using process_pipeline.Geometry;
using process_pipeline.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Core
{
    /// <summary>
    /// 可逆人工核查操作统一接口（Commit 4.1）。
    /// 
    /// 架构设计说明：
    /// 1. 为什么需要抽象 IUserOperation：
    ///    在管线核查的人机交互（Human-in-the-loop）流程中，人工操作分为两类：
    ///    - 纯 UI 内存状态判读（如：将某条或多条存疑管线标记为“已忽略/待复核/已修复”、修改核查备注），
    ///      此类操作不改动 DWG 数据库，CAD 原生 UNDO 命令无法感知，必须由插件自身的 Undo/Redo 栈管理；
    ///    - 涉及 CAD 图元修改的复合修复操作（如：在面板中一键单条或批量翻转多段线流向），
    ///      既修改了 CAD 数据库 Entity，又变更了面板行的核查状态。
    /// 2. 通过统一的 Command 模式接口封装 Execute() 与 Undo()，使 HybridUndoRedoManager 能够以一致的方式
    ///    执行、回滚和重做任何单条或批量人工核查动作。
    /// </summary>
    public interface IUserOperation
    {
        /// <summary>
        /// 操作的人类可读描述（用于 UI 状态栏反馈及撤销/重做按钮提示，如“批量翻转 5 条管线流向”）。
        /// </summary>
        string Description { get; }

        /// <summary>
        /// 标识该操作是否修改了 CAD 图面数据库（True = 涉及 CAD 实体事务修改；False = 仅修改内存判读状态）。
        /// </summary>
        bool TouchesCadDatabase { get; }

        /// <summary>
        /// 关联的目标 CAD 图元 ObjectId（批量操作或无特定图元时可为首个图元或 ObjectId.Null）。
        /// </summary>
        ObjectId TargetObjectId { get; }

        /// <summary>
        /// 执行（或重做 Redo）该操作。
        /// </summary>
        void Execute();

        /// <summary>
        /// 撤销（Undo）该操作，恢复到执行前的状态快照。
        /// </summary>
        void Undo();
    }

    /// <summary>
    /// 混合撤销/重做管理器（Commit 4.1）。
    /// 
    /// 架构设计说明：
    /// 1. 每个文档会话（<see cref="DocumentCheckSession"/>）拥有自己独立的 <see cref="HybridUndoRedoManager"/> 实例，
    ///    确保在多张 DWG 图纸之间切换时，各自的 Undo/Redo 操作历史栈完全隔离、互不污染。
    /// 2. 提供 <see cref="StateChanged"/> 事件，当栈状态变化时通知 WPF 主窗口刷新“撤销/重做”按钮的可用性（CanExecute）与提示文本。
    /// </summary>
    public class HybridUndoRedoManager
    {
        private readonly Stack<IUserOperation> _undoStack = new Stack<IUserOperation>();
        private readonly Stack<IUserOperation> _redoStack = new Stack<IUserOperation>();

        /// <summary>
        /// 当 Undo/Redo 栈发生变化时触发（用于通知 UI 刷新按钮状态）。
        /// </summary>
        public event EventHandler StateChanged;

        /// <summary>
        /// 当前是否可以执行撤销。
        /// </summary>
        public bool CanUndo => _undoStack.Count > 0;

        /// <summary>
        /// 当前是否可以执行重做。
        /// </summary>
        public bool CanRedo => _redoStack.Count > 0;

        /// <summary>
        /// 当前可撤销的操作数量。
        /// </summary>
        public int UndoCount => _undoStack.Count;

        /// <summary>
        /// 当前可重做的操作数量。
        /// </summary>
        public int RedoCount => _redoStack.Count;

        /// <summary>
        /// 下一步即将撤销的操作描述（若无可撤销操作则返回空字符串）。
        /// </summary>
        public string NextUndoDescription => _undoStack.Count > 0 ? _undoStack.Peek().Description : string.Empty;

        /// <summary>
        /// 下一步即将重做的操作描述（若无可重做操作则返回空字符串）。
        /// </summary>
        public string NextRedoDescription => _redoStack.Count > 0 ? _redoStack.Peek().Description : string.Empty;

        /// <summary>
        /// 执行一个新的人工可逆操作，并将其压入撤销栈，同时清空重做栈。
        /// </summary>
        public void ExecuteOperation(IUserOperation operation)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            operation.Execute();
            _undoStack.Push(operation);
            _redoStack.Clear();
            OnStateChanged();
        }

        /// <summary>
        /// 将一个已经在外部完成首次执行的操作直接记录到撤销栈中，并清空重做栈。
        /// </summary>
        public void RecordExecutedOperation(IUserOperation operation)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            _undoStack.Push(operation);
            _redoStack.Clear();
            OnStateChanged();
        }

        /// <summary>
        /// 撤销最近一次人工操作，并将其移入重做栈。
        /// </summary>
        /// <returns>被撤销的操作实例；若栈为空则返回 null。</returns>
        public IUserOperation Undo()
        {
            if (_undoStack.Count == 0)
                return null;

            IUserOperation op = _undoStack.Pop();
            op.Undo();
            _redoStack.Push(op);
            OnStateChanged();
            return op;
        }

        /// <summary>
        /// 重做最近一次被撤销的人工操作，并将其移回撤销栈。
        /// </summary>
        /// <returns>被重做的操作实例；若栈为空则返回 null。</returns>
        public IUserOperation Redo()
        {
            if (_redoStack.Count == 0)
                return null;

            IUserOperation op = _redoStack.Pop();
            op.Execute();
            _undoStack.Push(op);
            OnStateChanged();
            return op;
        }

        /// <summary>
        /// 清空当前撤销与重做栈（如重新执行全量核查或关闭文档时调用）。
        /// </summary>
        public void Clear()
        {
            if (_undoStack.Count == 0 && _redoStack.Count == 0)
                return;

            _undoStack.Clear();
            _redoStack.Clear();
            OnStateChanged();
        }

        private void OnStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 人工核查状态与属性修改操作（支持单条与批量，Commit 4.2 & Commit 5.5）。
    /// </summary>
    public class ChangeIssueStatusOperation : IUserOperation
    {
        private readonly List<(CheckIssueItem Before, CheckIssueItem After, Action<CheckIssueItem> ApplyAction)> _items;

        public string Description { get; }
        public bool TouchesCadDatabase => false;
        public ObjectId TargetObjectId => _items.Count > 0 && _items[0].After != null ? _items[0].After.TargetObjectId : ObjectId.Null;

        public ChangeIssueStatusOperation(
            CheckIssueItem beforeSnapshot,
            CheckIssueItem afterSnapshot,
            Action<CheckIssueItem> applyIssueAction,
            string description = null)
            : this(new[] { (beforeSnapshot, afterSnapshot, applyIssueAction) }, description)
        {
        }

        public ChangeIssueStatusOperation(
            IList<(CheckIssueItem Before, CheckIssueItem After, Action<CheckIssueItem> ApplyAction)> items,
            string description = null)
        {
            if (items == null || items.Count == 0)
                throw new ArgumentException("修改项集合不能为空", nameof(items));

            _items = items
                .Where(x => x.Before != null && x.After != null && x.ApplyAction != null)
                .Select(x => (x.Before.CloneSnapshot(), x.After.CloneSnapshot(), x.ApplyAction))
                .ToList();

            if (!string.IsNullOrWhiteSpace(description))
            {
                Description = description;
            }
            else if (_items.Count == 1)
            {
                var first = _items[0].After;
                string handleText = !string.IsNullOrWhiteSpace(first.TargetHandle) ? first.TargetHandle : first.IssueId;
                Description = $"修改 {handleText} 状态为“{first.GetStatusDisplayText()}”";
            }
            else
            {
                var first = _items[0].After;
                Description = $"批量修改 {_items.Count} 条管线状态为“{first.GetStatusDisplayText()}”";
            }
        }

        public void Execute()
        {
            foreach (var item in _items)
            {
                item.ApplyAction(item.After.CloneSnapshot());
            }
        }

        public void Undo()
        {
            foreach (var item in _items)
            {
                item.ApplyAction(item.Before.CloneSnapshot());
            }
        }
    }

    /// <summary>
    /// 翻转 CAD 管线（Curve/Polyline/Line）流向并联动更新 UI 核查状态的复合可逆操作（支持单条与批量，Commit 4.2 & Commit 5.5）。
    /// 
    /// 架构设计说明：
    /// 1. 结合了 CAD 数据库实体批量修改（在单次 DocumentLock 与单个 Transaction 内遍历执行 Curve.ReverseCurve）与 UI 核查状态回滚。
    /// 2. 由于 Curve.ReverseCurve() 具有自反性（连续翻转两次即恢复原方向），
    ///    Execute() 与 Undo() 均可在同一个事务中安全地批量反转所有选中管线的顶点顺序并一次性 Regen()，
    ///    按一次 Ctrl+Z 即可整批撤销所有反转的管线并恢复各自的原始核查状态。
    /// </summary>
    public class ReverseCurveOperation : IUserOperation
    {
        private readonly List<(ObjectId CurveId, CheckIssueItem BeforeSnapshot)> _items;
        private readonly Action<IReadOnlyList<(ObjectId CurveId, CheckIssueItem Snapshot)>, bool> _onBatchStateRestoredOrChanged;

        public string Description { get; }
        public bool TouchesCadDatabase => true;
        public ObjectId TargetObjectId => _items.Count > 0 ? _items[0].CurveId : ObjectId.Null;

        public ReverseCurveOperation(
            ObjectId curveId,
            CheckIssueItem beforeSnapshot,
            Action<ObjectId, CheckIssueItem, bool> onStateRestoredOrChanged)
            : this(
                new[] { (curveId, beforeSnapshot) },
                (batchList, isUndo) =>
                {
                    if (onStateRestoredOrChanged != null && batchList.Count > 0)
                    {
                        onStateRestoredOrChanged(batchList[0].CurveId, batchList[0].Snapshot, isUndo);
                    }
                })
        {
        }

        public ReverseCurveOperation(
            IList<(ObjectId CurveId, CheckIssueItem BeforeSnapshot)> items,
            Action<IReadOnlyList<(ObjectId CurveId, CheckIssueItem Snapshot)>, bool> onBatchStateRestoredOrChanged)
        {
            if (items == null || items.Count == 0)
                throw new ArgumentException("待翻转的管线集合不能为空", nameof(items));

            _items = items
                .Where(x => !x.CurveId.IsNull)
                .Select(x => (x.CurveId, x.BeforeSnapshot?.CloneSnapshot()))
                .ToList();
            _onBatchStateRestoredOrChanged = onBatchStateRestoredOrChanged;

            if (_items.Count == 1)
            {
                var first = _items[0];
                string handleText = !string.IsNullOrWhiteSpace(first.BeforeSnapshot?.TargetHandle)
                    ? first.BeforeSnapshot.TargetHandle
                    : (!first.CurveId.IsNull ? first.CurveId.Handle.ToString() : "未知管线");
                Description = $"翻转管线 {handleText} 流向";
            }
            else
            {
                Description = $"批量翻转 {_items.Count} 条管线流向";
            }
        }

        public void Execute()
        {
            var reversedIds = TryReverseCurvesInCad(_items.Select(x => x.CurveId).ToList());
            if (reversedIds.Count == 0)
                return;

            var reversedSet = new HashSet<ObjectId>(reversedIds);
            var afterList = new List<(ObjectId CurveId, CheckIssueItem Snapshot)>(_items.Count);
            foreach (var item in _items)
            {
                if (!reversedSet.Contains(item.CurveId))
                    continue;

                CheckIssueItem afterSnapshot = item.BeforeSnapshot?.CloneSnapshot();
                if (afterSnapshot != null)
                {
                    afterSnapshot.Status = IssueReviewStatus.Resolved;
                    afterSnapshot.Remark = "已翻转管线流向（已修复）";
                }
                afterList.Add((item.CurveId, afterSnapshot));
            }

            _onBatchStateRestoredOrChanged?.Invoke(afterList, false);
        }

        public void Undo()
        {
            var reversedIds = TryReverseCurvesInCad(_items.Select(x => x.CurveId).ToList());
            if (reversedIds.Count == 0)
                return;

            var reversedSet = new HashSet<ObjectId>(reversedIds);
            var beforeList = new List<(ObjectId CurveId, CheckIssueItem Snapshot)>(_items.Count);
            foreach (var item in _items)
            {
                if (!reversedSet.Contains(item.CurveId))
                    continue;

                beforeList.Add((item.CurveId, item.BeforeSnapshot?.CloneSnapshot()));
            }

            _onBatchStateRestoredOrChanged?.Invoke(beforeList, true);
        }

        /// <summary>
        /// 在单次文档锁与单个事务保护下，批量反转多条目标 Curve（Line / Polyline 等）的起点与终点方向并一次性刷新显示。
        /// </summary>
        private static List<ObjectId> TryReverseCurvesInCad(IList<ObjectId> curveIds)
        {
            var reversed = new List<ObjectId>();
            if (curveIds == null || curveIds.Count == 0)
                return reversed;

            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return reversed;

            try
            {
                doc.Editor?.SetImpliedSelection(Array.Empty<ObjectId>());
            }
            catch
            {
                // 忽略预选集清理异常
            }

            using (process_pipeline.Utils.PaletteRefreshManager.SuppressEventsScope())
            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    try
                    {
                        foreach (ObjectId curveId in curveIds)
                        {
                            if (curveId.IsNull || !curveId.IsValid || curveId.IsErased)
                                continue;

                            Curve cv = tr.GetObject(curveId, OpenMode.ForWrite) as Curve;
                            if (cv == null)
                                continue;

                            cv.ReverseCurve();
                            reversed.Add(curveId);
                        }

                        if (reversed.Count > 0)
                        {
                            tr.Commit();
                            doc.Editor.Regen();
                        }
                        else
                        {
                            tr.Abort();
                        }
                    }
                    catch
                    {
                        tr.Abort();
                        reversed.Clear();
                    }
                }
            }

            return reversed;
        }
    }

    /// <summary>
    /// 管线与属性块 CAD Group + XData (PIPE_CHECK_LINK) 可逆绑定操作（支持单条与批量，Stage 3 Commit 3.2）。
    /// 
    /// 架构设计说明：
    /// 1. 双重图属持久化：
    ///    在创建或修复 CAD GroupDictionary 中 1:1 Group 的同时，向管线（Polyline/Line）与属性块（BlockReference）
    ///    双向写入 XData（注册应用名 PIPE_CHECK_LINK，记录配对实体的十六进制 Handle）。
    ///    即使后续跨图复制粘贴（Ctrl+C/V）或写块（WBLOCK）导致原生 GroupDictionary 丢失，
    ///    GeometryHelper 与 PipeSpatialMatcher 依然可通过 XData 100% 恢复配对关系。
    /// 2. 完整可逆快照（Undo / Redo）：
    ///    首次执行时自动记录目标管线与属性块的原始 XData 值以及受影响的既有 Group 成员快照；
    ///    调用 Undo() 时自动擦除新建 Group、还原被修改的既有 Group 成员、清除或恢复原始 XData，并回滚 UI 核查行状态。
    /// </summary>
    public class BindPipeGroupOperation : IUserOperation
    {
        private sealed class BindPairExecutionRecord
        {
            public ObjectId PipeId { get; set; }
            public ObjectId AttachmentId { get; set; }
            public string PipeHandle { get; set; }
            public string AttachmentHandle { get; set; }
            public CheckIssueItem BeforeSnapshot { get; set; }
            public bool HasCapturedInitialXData { get; set; }
            public string PreviousPipeXDataHandle { get; set; } = string.Empty;
            public string PreviousAttachmentXDataHandle { get; set; } = string.Empty;
            public ObjectId CreatedGroupId { get; set; } = ObjectId.Null;
            public string CreatedGroupName { get; set; } = string.Empty;
            public bool LastExecuteSucceeded { get; set; }
        }

        private sealed class ModifiedGroupSnapshot
        {
            public ObjectId GroupId { get; set; }
            public string GroupName { get; set; }
            public ObjectId[] OriginalMemberIds { get; set; }
        }

        private readonly List<BindPairExecutionRecord> _records;
        private readonly List<ModifiedGroupSnapshot> _modifiedGroups = new List<ModifiedGroupSnapshot>();
        private readonly Action<IReadOnlyList<(ObjectId PipeId, CheckIssueItem Snapshot)>, bool> _onBatchStateRestoredOrChanged;
        private readonly Database _targetDatabase;

        public string Description { get; }
        public bool TouchesCadDatabase => true;
        public ObjectId TargetObjectId => _records.Count > 0 ? _records[0].PipeId : ObjectId.Null;

        /// <summary>
        /// 最近一次 Execute 成功绑定的管线数量。
        /// </summary>
        public int LastBoundCount => _records.Count(r => r.LastExecuteSucceeded);

        public BindPipeGroupOperation(
            ObjectId pipeId,
            ObjectId attachmentId,
            CheckIssueItem beforeSnapshot = null,
            Action<ObjectId, CheckIssueItem, bool> onStateRestoredOrChanged = null,
            Database targetDatabase = null)
            : this(
                new[] { (pipeId, attachmentId, beforeSnapshot) },
                (batchList, isUndo) =>
                {
                    if (onStateRestoredOrChanged != null && batchList.Count > 0)
                    {
                        onStateRestoredOrChanged(batchList[0].PipeId, batchList[0].Snapshot, isUndo);
                    }
                },
                targetDatabase)
        {
        }

        public BindPipeGroupOperation(
            IList<(ObjectId PipeId, ObjectId AttachmentId, CheckIssueItem BeforeSnapshot)> items,
            Action<IReadOnlyList<(ObjectId PipeId, CheckIssueItem Snapshot)>, bool> onBatchStateRestoredOrChanged = null,
            Database targetDatabase = null)
        {
            if (items == null || items.Count == 0)
                throw new ArgumentException("待绑定的管线-属性块配对集合不能为空", nameof(items));

            var seenEntities = new HashSet<ObjectId>();
            _records = new List<BindPairExecutionRecord>(items.Count);
            foreach (var x in items)
            {
                if (x.PipeId.IsNull || x.AttachmentId.IsNull || x.PipeId == x.AttachmentId)
                    continue;

                // 确保单次批量操作中每个管线与属性块最多只参与 1 组 1:1 Group 绑定，杜绝同一实体被重复打开或追加到多个 Group
                if (!seenEntities.Add(x.PipeId) || !seenEntities.Add(x.AttachmentId))
                    continue;

                _records.Add(new BindPairExecutionRecord
                {
                    PipeId = x.PipeId,
                    AttachmentId = x.AttachmentId,
                    PipeHandle = x.PipeId.IsValid ? x.PipeId.Handle.ToString() : string.Empty,
                    AttachmentHandle = x.AttachmentId.IsValid ? x.AttachmentId.Handle.ToString() : string.Empty,
                    BeforeSnapshot = x.BeforeSnapshot?.CloneSnapshot()
                });
            }

            if (_records.Count == 0)
                throw new ArgumentException("待绑定的管线-属性块配对集合中没有有效 ObjectId", nameof(items));

            _onBatchStateRestoredOrChanged = onBatchStateRestoredOrChanged;
            _targetDatabase = targetDatabase;

            if (_records.Count == 1)
            {
                var first = _records[0];
                Description = $"绑定管线 [{first.PipeHandle}] 与属性块 [{first.AttachmentHandle}] 的 Group 及 XData";
            }
            else
            {
                Description = $"批量绑定 {_records.Count} 组管线与属性块 Group 及 XData";
            }
        }

        public void Execute()
        {
            Database db = ResolveTargetDatabase();
            if (db == null)
                return;

            Document activeDoc = null;
            try
            {
                activeDoc = AcadApp.DocumentManager?.MdiActiveDocument;
            }
            catch
            {
                activeDoc = null;
            }

            bool needDocLock = activeDoc != null && activeDoc.Database == db;
            DocumentLock docLock = needDocLock ? activeDoc.LockDocument() : null;

            try
            {
                if (needDocLock)
                {
                    try
                    {
                        activeDoc.Editor?.SetImpliedSelection(Array.Empty<ObjectId>());
                    }
                    catch
                    {
                        // 忽略无头或命令上下文中清空 Pickfirst 选择集异常
                    }
                }

                _modifiedGroups.Clear();
                foreach (var rec in _records)
                {
                    rec.LastExecuteSucceeded = false;
                    rec.CreatedGroupId = ObjectId.Null;
                    rec.CreatedGroupName = string.Empty;
                }

                using (process_pipeline.Utils.PaletteRefreshManager.SuppressEventsScope())
                {
                    var validRecords = new List<BindPairExecutionRecord>(_records.Count);

                    // 阶段 1（XData 事务）：独立打开管线与属性块图元写入双向 PIPE_CHECK_LINK XData 并立即提交关闭，
                    // 杜绝在 AppTransaction 持有 Entity(ForWrite) 期间调用 AcDbGroup::append / remove（内部直接调用 acdbOpenObject）引发的对象状态冲突与内存越界。
                    using (Transaction trXData = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            PipeSpatialMatcher.EnsureRegAppRegistered(db, trXData);

                            foreach (BindPairExecutionRecord rec in _records)
                            {
                                if (!rec.PipeId.IsValid || rec.PipeId.IsErased ||
                                    !rec.AttachmentId.IsValid || rec.AttachmentId.IsErased)
                                {
                                    continue;
                                }

                                Entity pipeEnt = trXData.GetObject(rec.PipeId, OpenMode.ForWrite, false) as Entity;
                                Entity attEnt = trXData.GetObject(rec.AttachmentId, OpenMode.ForWrite, false) as Entity;
                                if (pipeEnt == null || attEnt == null)
                                    continue;

                                rec.PipeHandle = rec.PipeId.Handle.ToString();
                                rec.AttachmentHandle = rec.AttachmentId.Handle.ToString();

                                if (!rec.HasCapturedInitialXData)
                                {
                                    rec.PreviousPipeXDataHandle = PipeSpatialMatcher.ReadLinkedHandleFromXData(pipeEnt);
                                    rec.PreviousAttachmentXDataHandle = PipeSpatialMatcher.ReadLinkedHandleFromXData(attEnt);
                                    rec.HasCapturedInitialXData = true;
                                }

                                PipeSpatialMatcher.WriteLinkedHandleToXData(pipeEnt, rec.AttachmentHandle);
                                PipeSpatialMatcher.WriteLinkedHandleToXData(attEnt, rec.PipeHandle);
                                validRecords.Add(rec);
                            }

                            if (validRecords.Count > 0)
                            {
                                trXData.Commit();
                            }
                            else
                            {
                                trXData.Abort();
                                return;
                            }
                        }
                        catch
                        {
                            trXData.Abort();
                            throw;
                        }
                    }

                    // 阶段 2（GroupDictionary 事务）：在不打开任何 Entity(ForWrite) 的前提下执行 Group 剥离与 1:1 新建绑定
                    using (Transaction trGroup = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            DBDictionary groupDict = (DBDictionary)trGroup.GetObject(db.GroupDictionaryId, OpenMode.ForWrite);

                            var targetEntitySet = new HashSet<ObjectId>();
                            foreach (var rec in validRecords)
                            {
                                targetEntitySet.Add(rec.PipeId);
                                targetEntitySet.Add(rec.AttachmentId);
                            }

                            // 先将当前 GroupDictionary 条目快照到内存列表，避免在遍历 DBDictionary 迭代器的同时修改字典
                            var existingEntries = new List<KeyValuePair<string, ObjectId>>();
                            foreach (DBDictionaryEntry entry in groupDict)
                            {
                                existingEntries.Add(new KeyValuePair<string, ObjectId>(entry.Key, entry.Value));
                            }

                            // 1. 检查既有 Group 是否已引用本次待绑定的管线或属性块；若有则记录快照并先剥离旧引用
                            foreach (var entry in existingEntries)
                            {
                                if (entry.Value.IsNull || !entry.Value.IsValid || entry.Value.IsErased)
                                    continue;

                                Group existingGrp = trGroup.GetObject(entry.Value, OpenMode.ForRead, false) as Group;
                                if (existingGrp == null || existingGrp.IsErased)
                                    continue;

                                ObjectId[] memberIds = existingGrp.GetAllEntityIds();
                                if (memberIds == null || memberIds.Length == 0)
                                    continue;

                                if (memberIds.Any(id => targetEntitySet.Contains(id)))
                                {
                                    _modifiedGroups.Add(new ModifiedGroupSnapshot
                                    {
                                        GroupId = entry.Value,
                                        GroupName = entry.Key,
                                        OriginalMemberIds = memberIds.ToArray()
                                    });

                                    existingGrp.UpgradeOpen();
                                    foreach (ObjectId idToRemove in memberIds.Where(id => targetEntitySet.Contains(id)).ToList())
                                    {
                                        existingGrp.Remove(idToRemove);
                                    }
                                }
                            }

                            // 2. 为每一对 (Pipe, Attachment) 创建标准 1:1 Group（通过 ObjectIdCollection 一次性追加成员）
                            int boundCount = 0;
                            foreach (BindPairExecutionRecord rec in validRecords)
                            {
                                string baseGroupName = $"PIPE_GRP_{rec.PipeHandle}_{rec.AttachmentHandle}";
                                string groupName = AllocateGroupName(groupDict, baseGroupName);

                                var newGrp = new Group($"Pipe[{rec.PipeHandle}]-Attr[{rec.AttachmentHandle}]", true);
                                ObjectId grpId = groupDict.SetAt(groupName, newGrp);
                                trGroup.AddNewlyCreatedDBObject(newGrp, true);

                                var memberIds = new ObjectIdCollection
                                {
                                    rec.PipeId,
                                    rec.AttachmentId
                                };
                                newGrp.Append(memberIds);

                                rec.CreatedGroupId = grpId;
                                rec.CreatedGroupName = groupName;
                                rec.LastExecuteSucceeded = true;
                                boundCount++;
                            }

                            if (boundCount > 0)
                            {
                                trGroup.Commit();
                            }
                            else
                            {
                                trGroup.Abort();
                                return;
                            }
                        }
                        catch
                        {
                            trGroup.Abort();
                            throw;
                        }
                    }

                    // 确保 CAD 系统变量 PICKSTYLE 开启“编组选择”位（bit 1 = 1，对应快捷键 Ctrl+Shift+A），
                    // 否则当 PICKSTYLE=0 时用户在图面上点击已成组的管线或属性块时 CAD 不会整组联动选中。
                    try
                    {
                        object pickStyleObj = AcadApp.GetSystemVariable("PICKSTYLE");
                        if (pickStyleObj != null)
                        {
                            int pickStyle = Convert.ToInt32(pickStyleObj);
                            if ((pickStyle & 1) == 0)
                            {
                                AcadApp.SetSystemVariable("PICKSTYLE", (short)(pickStyle | 1));
                            }
                        }
                    }
                    catch
                    {
                        // 忽略无头或特殊环境下的系统变量设置异常
                    }
                }

                DocumentSessionManager.GetOrCreateSession(db)?.DatasetContext.InvalidatePreviousData();

                var afterList = new List<(ObjectId PipeId, CheckIssueItem Snapshot)>(_records.Count);
                foreach (BindPairExecutionRecord rec in _records)
                {
                    if (!rec.LastExecuteSucceeded)
                        continue;

                    CheckIssueItem afterSnapshot = rec.BeforeSnapshot?.CloneSnapshot();
                    if (afterSnapshot != null)
                    {
                        afterSnapshot.Status = IssueReviewStatus.Resolved;
                        afterSnapshot.Remark = $"已绑定 Group [{rec.CreatedGroupName}] 与 XData ({PipeSpatialMatcher.XDataRegAppName}:{rec.AttachmentHandle})（已修复）";
                    }
                    afterList.Add((rec.PipeId, afterSnapshot));
                }

                _onBatchStateRestoredOrChanged?.Invoke(afterList, false);
            }
            finally
            {
                docLock?.Dispose();
            }
        }

        public void Undo()
        {
            Database db = ResolveTargetDatabase();
            if (db == null)
                return;

            Document activeDoc = null;
            try
            {
                activeDoc = AcadApp.DocumentManager?.MdiActiveDocument;
            }
            catch
            {
                activeDoc = null;
            }

            bool needDocLock = activeDoc != null && activeDoc.Database == db;
            DocumentLock docLock = needDocLock ? activeDoc.LockDocument() : null;

            try
            {
                if (needDocLock)
                {
                    try
                    {
                        activeDoc.Editor?.SetImpliedSelection(Array.Empty<ObjectId>());
                    }
                    catch
                    {
                        // 忽略无头或命令上下文中清空 Pickfirst 选择集异常
                    }
                }

                using (process_pipeline.Utils.PaletteRefreshManager.SuppressEventsScope())
                {
                    // 阶段 1（Group 回滚事务）：仅打开 Group 对象执行 Clear / Erase 与原成员还原，不打开任何管线或属性块 Entity
                    using (Transaction trGroup = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            // 1. 擦除本次操作新建的 Group
                            foreach (BindPairExecutionRecord rec in _records)
                            {
                                if (!rec.CreatedGroupId.IsNull && rec.CreatedGroupId.IsValid && !rec.CreatedGroupId.IsErased)
                                {
                                    Group createdGrp = trGroup.GetObject(rec.CreatedGroupId, OpenMode.ForWrite, false) as Group;
                                    if (createdGrp != null)
                                    {
                                        createdGrp.Clear();
                                        createdGrp.Erase(true);
                                    }
                                }
                            }

                            // 2. 还原执行前被修改的既有 Group 成员
                            foreach (ModifiedGroupSnapshot mod in _modifiedGroups)
                            {
                                if (mod.GroupId.IsNull || !mod.GroupId.IsValid)
                                    continue;

                                Group origGrp = trGroup.GetObject(mod.GroupId, OpenMode.ForWrite, true) as Group;
                                if (origGrp == null)
                                    continue;

                                if (origGrp.IsErased)
                                {
                                    origGrp.Erase(false);
                                }

                                origGrp.Clear();
                                if (mod.OriginalMemberIds != null && mod.OriginalMemberIds.Length > 0)
                                {
                                    var memberCol = new ObjectIdCollection();
                                    foreach (ObjectId memberId in mod.OriginalMemberIds)
                                    {
                                        if (!memberId.IsNull && memberId.IsValid && !memberId.IsErased)
                                        {
                                            memberCol.Add(memberId);
                                        }
                                    }
                                    if (memberCol.Count > 0)
                                    {
                                        origGrp.Append(memberCol);
                                    }
                                }
                            }

                            trGroup.Commit();
                        }
                        catch
                        {
                            trGroup.Abort();
                            throw;
                        }
                    }

                    // 阶段 2（XData 回滚事务）：独立打开管线与属性块还原或清除 PIPE_CHECK_LINK XData
                    using (Transaction trXData = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            PipeSpatialMatcher.EnsureRegAppRegistered(db, trXData);

                            foreach (BindPairExecutionRecord rec in _records)
                            {
                                if (rec.PipeId.IsValid && !rec.PipeId.IsErased)
                                {
                                    Entity pipeEnt = trXData.GetObject(rec.PipeId, OpenMode.ForWrite, false) as Entity;
                                    if (pipeEnt != null)
                                    {
                                        if (string.IsNullOrWhiteSpace(rec.PreviousPipeXDataHandle))
                                            PipeSpatialMatcher.ClearLinkedHandleFromXData(pipeEnt);
                                        else
                                            PipeSpatialMatcher.WriteLinkedHandleToXData(pipeEnt, rec.PreviousPipeXDataHandle);
                                    }
                                }

                                if (rec.AttachmentId.IsValid && !rec.AttachmentId.IsErased)
                                {
                                    Entity attEnt = trXData.GetObject(rec.AttachmentId, OpenMode.ForWrite, false) as Entity;
                                    if (attEnt != null)
                                    {
                                        if (string.IsNullOrWhiteSpace(rec.PreviousAttachmentXDataHandle))
                                            PipeSpatialMatcher.ClearLinkedHandleFromXData(attEnt);
                                        else
                                            PipeSpatialMatcher.WriteLinkedHandleToXData(attEnt, rec.PreviousAttachmentXDataHandle);
                                    }
                                }
                            }

                            trXData.Commit();
                        }
                        catch
                        {
                            trXData.Abort();
                            throw;
                        }
                    }
                }

                DocumentSessionManager.GetOrCreateSession(db)?.DatasetContext.InvalidatePreviousData();

                var beforeList = new List<(ObjectId PipeId, CheckIssueItem Snapshot)>(_records.Count);
                foreach (BindPairExecutionRecord rec in _records)
                {
                    beforeList.Add((rec.PipeId, rec.BeforeSnapshot?.CloneSnapshot()));
                }

                _onBatchStateRestoredOrChanged?.Invoke(beforeList, true);
            }
            finally
            {
                docLock?.Dispose();
            }
        }

        private Database ResolveTargetDatabase()
        {
            if (_targetDatabase != null)
                return _targetDatabase;

            foreach (var rec in _records)
            {
                if (!rec.PipeId.IsNull && rec.PipeId.Database != null)
                    return rec.PipeId.Database;
                if (!rec.AttachmentId.IsNull && rec.AttachmentId.Database != null)
                    return rec.AttachmentId.Database;
            }

            try
            {
                return AcadApp.DocumentManager?.MdiActiveDocument?.Database;
            }
            catch
            {
                return null;
            }
        }

        private static string AllocateGroupName(DBDictionary groupDict, string baseName)
        {
            if (!groupDict.Contains(baseName))
                return baseName;

            int suffix = 1;
            while (groupDict.Contains($"{baseName}_{suffix}"))
            {
                suffix++;
            }
            return $"{baseName}_{suffix}";
        }
    }
}
