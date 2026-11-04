using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
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
}
