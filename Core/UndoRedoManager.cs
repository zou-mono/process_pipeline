using Autodesk.AutoCAD.DatabaseServices;
using System;
using System.Collections.Generic;

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
}

