using Autodesk.AutoCAD.Runtime;
using process_pipeline.Core;
using process_pipeline.Core.PreCheck;

namespace process_pipeline.Commands
{
    /// <summary>
    /// 单文档规划数据自身合规核查命令入口（Stage 4 Commit 4.2）。
    /// 
    /// 交互规范：
    /// 1. 严格以当前激活的 DWG 文档（MdiActiveDocument）作为唯一输入数据源，无需设置外部数据源；
    /// 2. 执行“颜色优先级高于图层名”一致性校验（检出如 3-污水管-现状 中的绿色管线 139, 213, 241, 242）、
    ///    退化零长度几何校验（如 Handle=184, 188, 1C8）以及残留旧图层校验（如 0污水-已核实需新增、0污水-属性）；
    /// 3. 将所有有效规划管线的分类结果（CATEGORY_ID / GHZT）同步写入当前文档会话的 PipeDatasetContext，
    ///    并将异常项推送至主核查窗口 ucMainCheckWindow，支持图元颜色/图层修改或删除后的毫秒级增量刷新。
    /// </summary>
    public class CheckPlanDataCommands : CadCommandBase
    {
        [CommandMethod("CHECKPLAN", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
        public override void Execute()
        {
            if (Doc == null) return;

            var service = new PlanDataCheckService(Doc.Database, Doc.Editor);
            service.Run(CadConfig.TaskNames.CheckPlan, bOnlyUpdate: false);
        }
    }
}
