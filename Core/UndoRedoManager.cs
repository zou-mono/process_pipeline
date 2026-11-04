using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using process_pipeline.Models;
using System;
using System.Collections.Generic;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace process_pipeline.Core
{
    /// <summary>
    /// 可逆人工核查操作统一接口（Commit 4.1）。
    /// 
    /// 架构设计说明：
    /// 1. 为什么需要抽象 IUserOperation：
    ///    在管线核查的人机交互（Human-in-the-loop）流程中，人工操作分为两类：
    ///    - 纯 UI 内存状态判读（如：将某条存疑管线标记为“已忽略/已修改/待复核”、修改核查备注），
    ///      此类操作不改动 DWG 数据库，CAD 原生 UNDO 命令无法感知，必须由插件自身的 Undo/Redo 栈管理；
    ///    - 涉及 CAD 图元修改的复合修复操作（如：在面板中一键翻转多段线流向、修改管径/高程属性），
    ///      既修改了 CAD 数据库 Entity，又变更了面板行的核查状态。
    /// 2. 通过统一的 Command 模式接口封装 Execute() 与 Undo()，使 HybridUndoRedoManager 能够以一致的方式
    ///    执行、回滚和重做任何人工核查动作。
    /// </summary>
    public interface IUserOperation
    {
        /// <summary>
        /// 操作的人类可读描述（用于 UI 状态栏反馈及撤销/重做按钮提示，如“标记 7B2A 为已忽略”）。
        /// </summary>
        string Description { get; }

        /// <summary>
        /// 标识该操作是否修改了 CAD 图面数据库（True = 涉及 CAD 实体事务修改；False = 仅修改内存判读状态）。
        /// </summary>
        bool TouchesCadDatabase { get; }

        /// <summary>
        /// 关联的目标 CAD 图元 ObjectId（无特定图元时可为 ObjectId.Null）。
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
    /// 人工核查状态与属性修改操作（Commit 4.2）。
    /// 
    /// 架构设计说明：
    /// 1. 针对纯 UI 内存层面的判读操作（如：将存疑管线标记为“已修改/待复核/已忽略”、修改管径/高程/备注），
    ///    由于不触发 CAD 数据库事务，原生 UNDO 无法撤销此类动作。
    /// 2. 本操作在构造时通过 <see cref="CheckIssueItem.CloneSnapshot"/> 保存修改前的完整问题快照与修改后的目标快照，
    ///    在 Execute() 与 Undo() 时通过委托回调触发对应表格行的单行就地刷新（不重建整个表格）。
    /// </summary>
    public class ChangeIssueStatusOperation : IUserOperation
    {
        private readonly CheckIssueItem _beforeSnapshot;
        private readonly CheckIssueItem _afterSnapshot;
        private readonly Action<CheckIssueItem> _applyIssueAction;

        public string Description { get; }
        public bool TouchesCadDatabase => false;
        public ObjectId TargetObjectId => _afterSnapshot != null ? _afterSnapshot.TargetObjectId : ObjectId.Null;

        public ChangeIssueStatusOperation(
            CheckIssueItem beforeSnapshot,
            CheckIssueItem afterSnapshot,
            Action<CheckIssueItem> applyIssueAction,
            string description = null)
        {
            if (beforeSnapshot == null) throw new ArgumentNullException(nameof(beforeSnapshot));
            if (afterSnapshot == null) throw new ArgumentNullException(nameof(afterSnapshot));
            if (applyIssueAction == null) throw new ArgumentNullException(nameof(applyIssueAction));

            _beforeSnapshot = beforeSnapshot.CloneSnapshot();
            _afterSnapshot = afterSnapshot.CloneSnapshot();
            _applyIssueAction = applyIssueAction;

            string handleText = !string.IsNullOrWhiteSpace(_afterSnapshot.TargetHandle)
                ? _afterSnapshot.TargetHandle
                : _afterSnapshot.IssueId;
            Description = !string.IsNullOrWhiteSpace(description)
                ? description
                : $"修改 {handleText} 状态为“{_afterSnapshot.GetStatusDisplayText()}”";
        }

        public void Execute()
        {
            _applyIssueAction(_afterSnapshot.CloneSnapshot());
        }

        public void Undo()
        {
            _applyIssueAction(_beforeSnapshot.CloneSnapshot());
        }
    }

    /// <summary>
    /// 翻转 CAD 管线（Curve/Polyline/Line）流向并联动更新 UI 核查状态的复合可逆操作（Commit 4.2）。
    /// 
    /// 架构设计说明：
    /// 1. 结合了 CAD 数据库实体修改（Curve.ReverseCurve）与 UI 核查状态回滚。
    /// 2. 由于 Curve.ReverseCurve() 具有自反性（连续翻转两次即恢复原方向），
    ///    Execute() 与 Undo() 均可在 DocumentLock + Transaction 中安全地反转目标管线顶点顺序：
    ///    - Execute() 翻转图元后，将核查项状态更新为“已修改（Modified）”或触发局部重算；
    ///    - Undo() 再次翻转图元恢复原始几何流向，并将主窗口中该行的核查项精确回滚为翻转前的快照。
    /// </summary>
    public class ReverseCurveOperation : IUserOperation
    {
        private readonly ObjectId _curveId;
        private readonly CheckIssueItem _beforeSnapshot;
        private readonly Action<ObjectId, CheckIssueItem, bool> _onStateRestoredOrChanged;

        public string Description { get; }
        public bool TouchesCadDatabase => true;
        public ObjectId TargetObjectId => _curveId;

        public ReverseCurveOperation(
            ObjectId curveId,
            CheckIssueItem beforeSnapshot,
            Action<ObjectId, CheckIssueItem, bool> onStateRestoredOrChanged)
        {
            _curveId = curveId;
            _beforeSnapshot = beforeSnapshot?.CloneSnapshot();
            _onStateRestoredOrChanged = onStateRestoredOrChanged;

            string handleText = !string.IsNullOrWhiteSpace(_beforeSnapshot?.TargetHandle)
                ? _beforeSnapshot.TargetHandle
                : (!curveId.IsNull ? curveId.Handle.ToString() : "未知管线");
            Description = $"翻转管线 {handleText} 流向";
        }

        public void Execute()
        {
            if (!TryReverseCurveInCad(_curveId))
                return;

            CheckIssueItem afterSnapshot = _beforeSnapshot?.CloneSnapshot();
            if (afterSnapshot != null)
            {
                afterSnapshot.Status = IssueReviewStatus.Resolved;
                afterSnapshot.Remark = "已翻转管线流向（已修复归档）";
            }

            _onStateRestoredOrChanged?.Invoke(_curveId, afterSnapshot, false);
        }

        public void Undo()
        {
            if (!TryReverseCurveInCad(_curveId))
                return;

            _onStateRestoredOrChanged?.Invoke(_curveId, _beforeSnapshot?.CloneSnapshot(), true);
        }

        /// <summary>
        /// 在文档锁与事务保护下安全反转目标 Curve（Line / Polyline 等）的起点与终点方向。
        /// </summary>
        private static bool TryReverseCurveInCad(ObjectId curveId)
        {
            if (curveId.IsNull || !curveId.IsValid || curveId.IsErased)
                return false;

            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return false;

            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    try
                    {
                        Curve cv = tr.GetObject(curveId, OpenMode.ForWrite) as Curve;
                        if (cv == null)
                            return false;

                        cv.ReverseCurve();
                        tr.Commit();
                        doc.Editor.Regen();
                        return true;
                    }
                    catch
                    {
                        tr.Abort();
                        return false;
                    }
                }
            }
        }
    }
}
