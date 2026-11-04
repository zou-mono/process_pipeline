using Autodesk.AutoCAD.DatabaseServices;
using process_pipeline.Models;
using System.Collections.Generic;

namespace process_pipeline.Core
{
    /// <summary>
    /// 管线核查规则统一接口。
    /// 
    /// 架构设计说明（Commit 1.2）：
    /// 1. 解耦“核查算法”与“UI 面板/刷新管理器”：
    ///    原有的 PaletteRefreshManager、GraphicManager 和 palCheckPipe 直接硬编码调用了 FlowArrowService。
    ///    引入 ICheckRule 后，UI 主窗口和后台增量刷新管理器只需面向此接口编程，
    ///    无论是 FlowArrow（流向箭头）、MatchPipe（管线匹配）还是 CompareSegment（线段覆盖），
    ///    都可以作为标准规则插件接入主核查窗口。
    /// 2. 支持“全量核查”与“人机交互局部增量核查”双模式：
    ///    - CheckAll：用于首次打开面板或点击“全量刷新”按钮；
    ///    - CheckIncremental：用于人工在 CAD 画布修改图元（如移动箭头、反转多段线）或
    ///      在 WPF 列表点击修复/撤销后，仅针对受影响的管线 ObjectId 进行极速局部重算。
    /// 3. 封装变更影响范围追踪（GetAffectedTargetIds）：
    ///    将“某个 CAD 实体被修改后，哪些管线需要重新核查”的拓扑判断下沉到具体规则内部，
    ///    避免数据库监听层（PaletteRefreshManager）混杂具体业务的图层与几何逻辑。
    /// </summary>
    public interface ICheckRule
    {
        /// <summary>
        /// 规则唯一标识（例如："FlowArrow"、"MatchPipe"、"CompareSegment"）。
        /// </summary>
        string RuleId { get; }

        /// <summary>
        /// 规则中文显示名称（例如："管线流向箭头核查"）。
        /// </summary>
        string RuleName { get; }

        /// <summary>
        /// 执行全量核查。
        /// </summary>
        /// <param name="context">进度条与 ESC 取消上下文（若在后台静默调用可传 null）</param>
        /// <returns>核查出的问题字典（Key 为目标管线 ObjectId，Value 为统一问题模型 CheckIssueItem）</returns>
        Dictionary<ObjectId, CheckIssueItem> CheckAll(ProgressContext context = null);

        /// <summary>
        /// 执行局部增量核查。
        /// 当用户在 CAD 图面手动修改图元、或在 UI 触发修复/Undo 后，仅对受影响的管线重新核查。
        /// </summary>
        /// <param name="targetIds">需要重新核查的目标管线 ObjectId 列表</param>
        /// <param name="currentIssues">当前会话已有的问题字典（增量核查将在此字典基础上原地更新或标记 Resolved）</param>
        /// <param name="context">进度条与取消上下文（局部重算通常传 null）</param>
        /// <returns>增量更新后的问题字典</returns>
        Dictionary<ObjectId, CheckIssueItem> CheckIncremental(
            List<ObjectId> targetIds,
            Dictionary<ObjectId, CheckIssueItem> currentIssues = null,
            ProgressContext context = null);

        /// <summary>
        /// 当 CAD 数据库中的某个对象发生增、删、改时，判断该对象是否与本核查规则相关，
        /// 并返回受其影响、需要重新核查的主管线 ObjectId 集合。
        /// </summary>
        /// <param name="changedObj">发生变化的 CAD 数据库对象（Entity / DBObject）</param>
        /// <returns>受影响的主管线 ObjectId 集合；若与本规则无关则返回空集合</returns>
        IEnumerable<ObjectId> GetAffectedTargetIds(DBObject changedObj);
    }
}

