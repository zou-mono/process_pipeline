using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Microsoft.Win32;
using process_pipeline.Core;
using process_pipeline.Core.DataSource;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadDb = Autodesk.AutoCAD.DatabaseServices;

namespace process_pipeline.Forms
{
    /// <summary>
    /// 已打开 MDI 文档候选项视图模型（供数据源设置窗口下拉列表选择，Stage 6 Commit 6.3）。
    /// </summary>
    public class MdiDocumentOptionItem
    {
        public string DisplayName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public AcadDb.Database Database { get; set; }
        public int PreviousLayerCurveCount { get; set; }
        public int SurveyLayerCurveCount { get; set; }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// 多数据源核查配置窗口的 MVVM 视图模型（Stage 6 Commit 6.3）。
    /// 
    /// 架构设计说明：
    /// 1. 与 WPF 窗口完全解耦，可在无 UI 测试环境（accoreconsole）下直接实例化验证三级智能嗅探、
    ///    数据源模式切换（同图图层 / 已打开 MDI 文档 / 外部 DWG 文件）以及 Config.json 双向路径持久化。
    /// 2. 以当前激活文档固定作为【规划数据】，独立配置【上版数据（核查期可编辑）】与【勘测数据（只读基准）】。
    /// </summary>
    public class DataSourceSettingsViewModel : INotifyPropertyChanged
    {
        private readonly AcadDb.Database _activePlanDb;
        private readonly PipeDatasetContext _datasetContext;
        private readonly DataSourceService _dataSourceService;

        private bool _preferSameDrawingLayers;
        private bool _preferOpenMdiDocuments;

        private DataSourceKind _previousSourceKind = DataSourceKind.ExternalDwgFile;
        private MdiDocumentOptionItem _selectedPreviousMdiDoc;
        private string _previousExternalFilePath = string.Empty;
        private int _previousSameDrawingCurveCount;

        private DataSourceKind _surveySourceKind = DataSourceKind.ExternalDwgFile;
        private MdiDocumentOptionItem _selectedSurveyMdiDoc;
        private string _surveyExternalFilePath = string.Empty;
        private int _surveySameDrawingCurveCount;

        private string _footerStatusText = "就绪：已加载当前会话与 Config.json 历史数据源配置";

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 请求关闭绑定窗口的事件（参数为 true 表示确认保存，false 表示取消）。
        /// </summary>
        public event Action<bool> RequestClose;

        // ============================================================
        // 1. 规划数据信息（当前激活主图，只读展示）
        // ============================================================

        public string PlanningDocDisplayName { get; private set; } = "当前激活图纸";
        public string PlanningFilePath { get; private set; } = string.Empty;
        public string PlanningLayersSummary { get; private set; } = string.Empty;
        public int PlanningCurveCount { get; private set; }
        public string PlanningStatusText =>
            $"主核查图纸：{PlanningDocDisplayName}（检测到规划管线 {PlanningCurveCount} 条）";

        // ============================================================
        // 2. 全局智能嗅探优先级开关（双向绑定 Config.json）
        // ============================================================

        public bool PreferSameDrawingLayers
        {
            get => _preferSameDrawingLayers;
            set
            {
                if (SetField(ref _preferSameDrawingLayers, value))
                {
                    RefreshValidationSummary();
                }
            }
        }

        public bool PreferOpenMdiDocuments
        {
            get => _preferOpenMdiDocuments;
            set
            {
                if (SetField(ref _preferOpenMdiDocuments, value))
                {
                    RefreshValidationSummary();
                }
            }
        }

        /// <summary>
        /// 当前 AutoCAD 进程中已打开的其他 MDI 文档列表（排除当前规划主图）。
        /// </summary>
        public ObservableCollection<MdiDocumentOptionItem> OpenMdiDocuments { get; } =
            new ObservableCollection<MdiDocumentOptionItem>();

        public bool HasOpenMdiDocuments => OpenMdiDocuments.Count > 0;

        // ============================================================
        // 3. 上版数据配置（核查过程中可编辑参考）
        // ============================================================

        public DataSourceKind PreviousSourceKind
        {
            get => _previousSourceKind;
            set
            {
                if (SetField(ref _previousSourceKind, value))
                {
                    OnPropertyChanged(nameof(IsPreviousSameDrawing));
                    OnPropertyChanged(nameof(IsPreviousOpenMdi));
                    OnPropertyChanged(nameof(IsPreviousExternalFile));
                    RefreshValidationSummary();
                }
            }
        }

        public bool IsPreviousSameDrawing
        {
            get => _previousSourceKind == DataSourceKind.SameDrawing;
            set
            {
                if (value) PreviousSourceKind = DataSourceKind.SameDrawing;
            }
        }

        public bool IsPreviousOpenMdi
        {
            get => _previousSourceKind == DataSourceKind.OpenMdiDocument;
            set
            {
                if (value) PreviousSourceKind = DataSourceKind.OpenMdiDocument;
            }
        }

        public bool IsPreviousExternalFile
        {
            get => _previousSourceKind == DataSourceKind.ExternalDwgFile || _previousSourceKind == DataSourceKind.Unresolved;
            set
            {
                if (value) PreviousSourceKind = DataSourceKind.ExternalDwgFile;
            }
        }

        public int PreviousSameDrawingCurveCount
        {
            get => _previousSameDrawingCurveCount;
            private set
            {
                if (SetField(ref _previousSameDrawingCurveCount, value))
                {
                    OnPropertyChanged(nameof(PreviousSameDrawingHintText));
                }
            }
        }

        public string PreviousSameDrawingHintText =>
            _previousSameDrawingCurveCount >= DataSourceService.MinSameDrawingEntityCount
                ? $"当前图纸内 [0污水-*] 图层已包含 {_previousSameDrawingCurveCount} 条管线（可直接合图核查）"
                : $"当前图纸内 [0污水-*] 图层仅 {_previousSameDrawingCurveCount} 条管线（少于合图阈值 {DataSourceService.MinSameDrawingEntityCount} 条）";

        public MdiDocumentOptionItem SelectedPreviousMdiDoc
        {
            get => _selectedPreviousMdiDoc;
            set
            {
                if (SetField(ref _selectedPreviousMdiDoc, value))
                {
                    if (value != null && _previousSourceKind != DataSourceKind.OpenMdiDocument)
                    {
                        PreviousSourceKind = DataSourceKind.OpenMdiDocument;
                    }
                    RefreshValidationSummary();
                }
            }
        }

        public string PreviousExternalFilePath
        {
            get => _previousExternalFilePath;
            set
            {
                if (SetField(ref _previousExternalFilePath, value ?? string.Empty))
                {
                    if (!string.IsNullOrWhiteSpace(_previousExternalFilePath) &&
                        _previousSourceKind != DataSourceKind.ExternalDwgFile)
                    {
                        PreviousSourceKind = DataSourceKind.ExternalDwgFile;
                    }
                    RefreshValidationSummary();
                }
            }
        }

        public bool IsPreviousSourceValid => BuildPreviousDescriptor().IsValid;

        public string PreviousValidationText
        {
            get
            {
                var desc = BuildPreviousDescriptor();
                if (!desc.IsValid)
                {
                    return "⚠ 未就绪：请指定有效的上版数据 DWG 文件或选择已打开文档";
                }
                return $"✓ 已就绪：{desc.DisplayName}";
            }
        }

        // ============================================================
        // 4. 勘测数据配置（只读合并基准）
        // ============================================================

        public DataSourceKind SurveySourceKind
        {
            get => _surveySourceKind;
            set
            {
                if (SetField(ref _surveySourceKind, value))
                {
                    OnPropertyChanged(nameof(IsSurveySameDrawing));
                    OnPropertyChanged(nameof(IsSurveyOpenMdi));
                    OnPropertyChanged(nameof(IsSurveyExternalFile));
                    RefreshValidationSummary();
                }
            }
        }

        public bool IsSurveySameDrawing
        {
            get => _surveySourceKind == DataSourceKind.SameDrawing;
            set
            {
                if (value) SurveySourceKind = DataSourceKind.SameDrawing;
            }
        }

        public bool IsSurveyOpenMdi
        {
            get => _surveySourceKind == DataSourceKind.OpenMdiDocument;
            set
            {
                if (value) SurveySourceKind = DataSourceKind.OpenMdiDocument;
            }
        }

        public bool IsSurveyExternalFile
        {
            get => _surveySourceKind == DataSourceKind.ExternalDwgFile || _surveySourceKind == DataSourceKind.Unresolved;
            set
            {
                if (value) SurveySourceKind = DataSourceKind.ExternalDwgFile;
            }
        }

        public int SurveySameDrawingCurveCount
        {
            get => _surveySameDrawingCurveCount;
            private set
            {
                if (SetField(ref _surveySameDrawingCurveCount, value))
                {
                    OnPropertyChanged(nameof(SurveySameDrawingHintText));
                }
            }
        }

        public string SurveySameDrawingHintText =>
            _surveySameDrawingCurveCount >= DataSourceService.MinSameDrawingEntityCount
                ? $"当前图纸内 [勘测管] 图层已包含 {_surveySameDrawingCurveCount} 条管线（可直接合图核查）"
                : $"当前图纸内 [勘测管] 图层仅 {_surveySameDrawingCurveCount} 条管线（少于合图阈值 {DataSourceService.MinSameDrawingEntityCount} 条）";

        public MdiDocumentOptionItem SelectedSurveyMdiDoc
        {
            get => _selectedSurveyMdiDoc;
            set
            {
                if (SetField(ref _selectedSurveyMdiDoc, value))
                {
                    if (value != null && _surveySourceKind != DataSourceKind.OpenMdiDocument)
                    {
                        SurveySourceKind = DataSourceKind.OpenMdiDocument;
                    }
                    RefreshValidationSummary();
                }
            }
        }

        public string SurveyExternalFilePath
        {
            get => _surveyExternalFilePath;
            set
            {
                if (SetField(ref _surveyExternalFilePath, value ?? string.Empty))
                {
                    if (!string.IsNullOrWhiteSpace(_surveyExternalFilePath) &&
                        _surveySourceKind != DataSourceKind.ExternalDwgFile)
                    {
                        SurveySourceKind = DataSourceKind.ExternalDwgFile;
                    }
                    RefreshValidationSummary();
                }
            }
        }

        public bool IsSurveySourceValid => BuildSurveyDescriptor().IsValid;

        public string SurveyValidationText
        {
            get
            {
                var desc = BuildSurveyDescriptor();
                if (!desc.IsValid)
                {
                    return "⚠ 未就绪：请指定有效的勘测数据 DWG 文件或选择已打开文档";
                }
                return $"✓ 已就绪：{desc.DisplayName}";
            }
        }

        // ============================================================
        // 5. 底部状态与命令
        // ============================================================

        public string FooterStatusText
        {
            get => _footerStatusText;
            set => SetField(ref _footerStatusText, value);
        }

        public bool DialogConfirmed { get; private set; }

        public ICommand AutoSniffCommand { get; }
        public ICommand BrowsePreviousFileCommand { get; }
        public ICommand BrowseSurveyFileCommand { get; }
        public ICommand ConfirmCommand { get; }
        public ICommand CancelCommand { get; }

        public DataSourceSettingsViewModel(
            AcadDb.Database activePlanDb,
            PipeDatasetContext datasetContext = null,
            DataSourceService dataSourceService = null,
            IEnumerable<MdiDocumentOptionItem> customMdiDocs = null)
        {
            _activePlanDb = activePlanDb;
            _datasetContext = datasetContext ?? (activePlanDb != null
                ? DocumentSessionManager.GetOrCreateSession(activePlanDb)?.DatasetContext
                : null) ?? new PipeDatasetContext();
            _dataSourceService = dataSourceService ?? new DataSourceService();

            _preferSameDrawingLayers = CadConfig.DataSources.PreferSameDrawingLayers;
            _preferOpenMdiDocuments = CadConfig.DataSources.PreferOpenMdiDocuments;

            AutoSniffCommand = new RelayCommand(_ => ExecuteAutoSniff());
            BrowsePreviousFileCommand = new RelayCommand(_ => BrowsePreviousFile());
            BrowseSurveyFileCommand = new RelayCommand(_ => BrowseSurveyFile());
            ConfirmCommand = new RelayCommand(_ => ConfirmAndSave());
            CancelCommand = new RelayCommand(_ => Cancel());

            LoadPlanningDrawingSummary();
            RefreshOpenMdiDocuments(customMdiDocs);
            InitializeFromContextOrSniff();
        }

        /// <summary>
        /// 统计当前激活规划图纸的基础图层与实体信息。
        /// </summary>
        private void LoadPlanningDrawingSummary()
        {
            var planLayers = CadConfig.PlanningData.GetAllPipeLayers().ToList();
            PlanningLayersSummary = string.Join(", ", planLayers);

            if (_activePlanDb == null)
            {
                PlanningDocDisplayName = "无活动文档";
                PlanningFilePath = string.Empty;
                PlanningCurveCount = 0;
                return;
            }

            try
            {
                string fn = _activePlanDb.Filename;
                PlanningFilePath = !string.IsNullOrWhiteSpace(fn) ? fn : string.Empty;
                PlanningDocDisplayName = !string.IsNullOrWhiteSpace(fn)
                    ? Path.GetFileName(fn)
                    : "当前未命名文档";
            }
            catch
            {
                PlanningDocDisplayName = "当前激活图纸";
            }

            PlanningCurveCount = DataSourceService.CountCurveEntitiesOnLayers(_activePlanDb, planLayers);
            PreviousSameDrawingCurveCount = DataSourceService.CountCurveEntitiesOnLayers(
                _activePlanDb,
                CadConfig.PreviousData.PipeLayers);
            SurveySameDrawingCurveCount = DataSourceService.CountCurveEntitiesOnLayers(
                _activePlanDb,
                CadConfig.SurveyData.PipeLayers);
        }

        /// <summary>
        /// 扫描当前 AutoCAD 进程中已打开的其他 MDI 文档（或注入测试模拟的 MDI 文档列表）。
        /// </summary>
        public void RefreshOpenMdiDocuments(IEnumerable<MdiDocumentOptionItem> customMdiDocs = null)
        {
            OpenMdiDocuments.Clear();

            if (customMdiDocs != null)
            {
                foreach (var item in customMdiDocs.Where(x => x != null))
                {
                    OpenMdiDocuments.Add(item);
                }
                OnPropertyChanged(nameof(HasOpenMdiDocuments));
                return;
            }

            try
            {
                var docManager = AcadApp.DocumentManager;
                if (docManager != null)
                {
                    foreach (Document doc in docManager)
                    {
                        if (doc?.Database == null || ReferenceEquals(doc.Database, _activePlanDb))
                            continue;

                        string filePath = string.Empty;
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(doc.Database.Filename))
                                filePath = doc.Database.Filename;
                        }
                        catch
                        {
                            // 忽略
                        }

                        string shortName = !string.IsNullOrWhiteSpace(filePath)
                            ? Path.GetFileName(filePath)
                            : Path.GetFileName(doc.Name ?? "未命名.dwg");

                        int prevCount = DataSourceService.CountCurveEntitiesOnLayers(
                            doc.Database,
                            CadConfig.PreviousData.PipeLayers,
                            stopAtCount: 500);
                        int surveyCount = DataSourceService.CountCurveEntitiesOnLayers(
                            doc.Database,
                            CadConfig.SurveyData.PipeLayers,
                            stopAtCount: 500);

                        OpenMdiDocuments.Add(new MdiDocumentOptionItem
                        {
                            Database = doc.Database,
                            FilePath = filePath,
                            PreviousLayerCurveCount = prevCount,
                            SurveyLayerCurveCount = surveyCount,
                            DisplayName = $"{shortName} (上版图层:{prevCount} / 勘测图层:{surveyCount})"
                        });
                    }
                }
            }
            catch
            {
                // 在无 UI 或独立测试环境下安全忽略
            }

            OnPropertyChanged(nameof(HasOpenMdiDocuments));
        }

        /// <summary>
        /// 初始化视图模型：优先回填当前会话或 Config.json 记忆路径，并自动补齐未解析项。
        /// </summary>
        private void InitializeFromContextOrSniff()
        {
            // 先预填 Config.json 记忆路径作为外部文件默认值
            if (!string.IsNullOrWhiteSpace(CadConfig.DataSources.LastPreviousDataDwgPath))
            {
                _previousExternalFilePath = CadConfig.DataSources.LastPreviousDataDwgPath;
            }
            if (!string.IsNullOrWhiteSpace(CadConfig.DataSources.LastSurveyDataDwgPath))
            {
                _surveyExternalFilePath = CadConfig.DataSources.LastSurveyDataDwgPath;
            }

            DataSourceDescriptor prevDesc = (_datasetContext?.PreviousDataSource != null && _datasetContext.PreviousDataSource.IsValid)
                ? _datasetContext.PreviousDataSource
                : _dataSourceService.ResolvePreviousDataSource(_activePlanDb, null);

            DataSourceDescriptor surveyDesc = (_datasetContext?.SurveyDataSource != null && _datasetContext.SurveyDataSource.IsValid)
                ? _datasetContext.SurveyDataSource
                : _dataSourceService.ResolveSurveyDataSource(_activePlanDb, null);

            ApplyPreviousDescriptorToVm(prevDesc);
            ApplySurveyDescriptorToVm(surveyDesc);
            RefreshValidationSummary();
        }

        /// <summary>
        /// 执行三级智能嗅探并将探测结果回填到当前视图模型。
        /// </summary>
        public void ExecuteAutoSniff()
        {
            bool origSame = CadConfig.DataSources.PreferSameDrawingLayers;
            bool origMdi = CadConfig.DataSources.PreferOpenMdiDocuments;
            try
            {
                CadConfig.DataSources.PreferSameDrawingLayers = PreferSameDrawingLayers;
                CadConfig.DataSources.PreferOpenMdiDocuments = PreferOpenMdiDocuments;

                DataSourceDescriptor prevDesc = _dataSourceService.ResolvePreviousDataSource(_activePlanDb, null);
                DataSourceDescriptor surveyDesc = _dataSourceService.ResolveSurveyDataSource(_activePlanDb, null);

                ApplyPreviousDescriptorToVm(prevDesc);
                ApplySurveyDescriptorToVm(surveyDesc);
                RefreshValidationSummary();

                FooterStatusText = $"智能探测完成：上版[{ (IsPreviousSourceValid ? "已就绪" : "未找到") }]，勘测[{ (IsSurveySourceValid ? "已就绪" : "未找到") }]";
            }
            finally
            {
                CadConfig.DataSources.PreferSameDrawingLayers = origSame;
                CadConfig.DataSources.PreferOpenMdiDocuments = origMdi;
            }
        }

        private void ApplyPreviousDescriptorToVm(DataSourceDescriptor desc)
        {
            if (desc == null || desc.Kind == DataSourceKind.Unresolved)
            {
                PreviousSourceKind = DataSourceKind.ExternalDwgFile;
                return;
            }

            switch (desc.Kind)
            {
                case DataSourceKind.SameDrawing:
                    PreviousSourceKind = DataSourceKind.SameDrawing;
                    break;
                case DataSourceKind.OpenMdiDocument:
                    var matchMdi = OpenMdiDocuments.FirstOrDefault(m =>
                        ReferenceEquals(m.Database, desc.InMemoryDatabase) ||
                        (!string.IsNullOrWhiteSpace(m.FilePath) &&
                         !string.IsNullOrWhiteSpace(desc.FilePath) &&
                         string.Equals(m.FilePath, desc.FilePath, StringComparison.OrdinalIgnoreCase)));
                    if (matchMdi != null)
                    {
                        SelectedPreviousMdiDoc = matchMdi;
                        PreviousSourceKind = DataSourceKind.OpenMdiDocument;
                    }
                    else if (!string.IsNullOrWhiteSpace(desc.FilePath))
                    {
                        PreviousExternalFilePath = desc.FilePath;
                        PreviousSourceKind = DataSourceKind.ExternalDwgFile;
                    }
                    break;
                case DataSourceKind.ExternalDwgFile:
                default:
                    if (!string.IsNullOrWhiteSpace(desc.FilePath))
                    {
                        PreviousExternalFilePath = desc.FilePath;
                    }
                    PreviousSourceKind = DataSourceKind.ExternalDwgFile;
                    break;
            }
        }

        private void ApplySurveyDescriptorToVm(DataSourceDescriptor desc)
        {
            if (desc == null || desc.Kind == DataSourceKind.Unresolved)
            {
                SurveySourceKind = DataSourceKind.ExternalDwgFile;
                return;
            }

            switch (desc.Kind)
            {
                case DataSourceKind.SameDrawing:
                    SurveySourceKind = DataSourceKind.SameDrawing;
                    break;
                case DataSourceKind.OpenMdiDocument:
                    var matchMdi = OpenMdiDocuments.FirstOrDefault(m =>
                        ReferenceEquals(m.Database, desc.InMemoryDatabase) ||
                        (!string.IsNullOrWhiteSpace(m.FilePath) &&
                         !string.IsNullOrWhiteSpace(desc.FilePath) &&
                         string.Equals(m.FilePath, desc.FilePath, StringComparison.OrdinalIgnoreCase)));
                    if (matchMdi != null)
                    {
                        SelectedSurveyMdiDoc = matchMdi;
                        SurveySourceKind = DataSourceKind.OpenMdiDocument;
                    }
                    else if (!string.IsNullOrWhiteSpace(desc.FilePath))
                    {
                        SurveyExternalFilePath = desc.FilePath;
                        SurveySourceKind = DataSourceKind.ExternalDwgFile;
                    }
                    break;
                case DataSourceKind.ExternalDwgFile:
                default:
                    if (!string.IsNullOrWhiteSpace(desc.FilePath))
                    {
                        SurveyExternalFilePath = desc.FilePath;
                    }
                    SurveySourceKind = DataSourceKind.ExternalDwgFile;
                    break;
            }
        }

        /// <summary>
        /// 根据当前视图模型状态构建【上版数据】描述符。
        /// </summary>
        public DataSourceDescriptor BuildPreviousDescriptor()
        {
            var pipeLayers = new List<string>(CadConfig.PreviousData.PipeLayers ?? new List<string>());
            var attrLayers = new List<string>(CadConfig.PreviousData.AttributeLayers ?? new List<string>());

            switch (_previousSourceKind)
            {
                case DataSourceKind.SameDrawing:
                    return new DataSourceDescriptor
                    {
                        Kind = _activePlanDb != null ? DataSourceKind.SameDrawing : DataSourceKind.Unresolved,
                        InMemoryDatabase = _activePlanDb,
                        FilePath = PlanningFilePath,
                        DisplayName = $"当前图纸内图层（0污水-*，{_previousSameDrawingCurveCount} 条）",
                        PipeLayers = pipeLayers,
                        AttachmentLayers = attrLayers
                    };

                case DataSourceKind.OpenMdiDocument:
                    if (_selectedPreviousMdiDoc?.Database != null)
                    {
                        string shortName = !string.IsNullOrWhiteSpace(_selectedPreviousMdiDoc.FilePath)
                            ? Path.GetFileName(_selectedPreviousMdiDoc.FilePath)
                            : _selectedPreviousMdiDoc.DisplayName;
                        return new DataSourceDescriptor
                        {
                            Kind = DataSourceKind.OpenMdiDocument,
                            InMemoryDatabase = _selectedPreviousMdiDoc.Database,
                            FilePath = _selectedPreviousMdiDoc.FilePath,
                            DisplayName = $"已打开文档: {shortName}",
                            PipeLayers = pipeLayers,
                            AttachmentLayers = attrLayers
                        };
                    }
                    break;

                case DataSourceKind.ExternalDwgFile:
                default:
                    string trimmed = (_previousExternalFilePath ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && File.Exists(trimmed))
                    {
                        string fullPath = Path.GetFullPath(trimmed);
                        return new DataSourceDescriptor
                        {
                            Kind = DataSourceKind.ExternalDwgFile,
                            FilePath = fullPath,
                            DisplayName = $"外部文件: {Path.GetFileName(fullPath)}",
                            PipeLayers = pipeLayers,
                            AttachmentLayers = attrLayers
                        };
                    }
                    break;
            }

            return new DataSourceDescriptor
            {
                Kind = DataSourceKind.Unresolved,
                FilePath = (_previousExternalFilePath ?? string.Empty).Trim(),
                DisplayName = "未指定有效上版数据",
                PipeLayers = pipeLayers,
                AttachmentLayers = attrLayers
            };
        }

        /// <summary>
        /// 根据当前视图模型状态构建【勘测数据】描述符。
        /// </summary>
        public DataSourceDescriptor BuildSurveyDescriptor()
        {
            var pipeLayers = new List<string>(CadConfig.SurveyData.PipeLayers ?? new List<string>());
            var attrLayers = new List<string>(CadConfig.SurveyData.AttributeLayers ?? new List<string>());

            switch (_surveySourceKind)
            {
                case DataSourceKind.SameDrawing:
                    return new DataSourceDescriptor
                    {
                        Kind = _activePlanDb != null ? DataSourceKind.SameDrawing : DataSourceKind.Unresolved,
                        InMemoryDatabase = _activePlanDb,
                        FilePath = PlanningFilePath,
                        DisplayName = $"当前图纸内图层（勘测管，{_surveySameDrawingCurveCount} 条）",
                        PipeLayers = pipeLayers,
                        AttachmentLayers = attrLayers
                    };

                case DataSourceKind.OpenMdiDocument:
                    if (_selectedSurveyMdiDoc?.Database != null)
                    {
                        string shortName = !string.IsNullOrWhiteSpace(_selectedSurveyMdiDoc.FilePath)
                            ? Path.GetFileName(_selectedSurveyMdiDoc.FilePath)
                            : _selectedSurveyMdiDoc.DisplayName;
                        return new DataSourceDescriptor
                        {
                            Kind = DataSourceKind.OpenMdiDocument,
                            InMemoryDatabase = _selectedSurveyMdiDoc.Database,
                            FilePath = _selectedSurveyMdiDoc.FilePath,
                            DisplayName = $"已打开文档: {shortName}",
                            PipeLayers = pipeLayers,
                            AttachmentLayers = attrLayers
                        };
                    }
                    break;

                case DataSourceKind.ExternalDwgFile:
                default:
                    string trimmed = (_surveyExternalFilePath ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && File.Exists(trimmed))
                    {
                        string fullPath = Path.GetFullPath(trimmed);
                        return new DataSourceDescriptor
                        {
                            Kind = DataSourceKind.ExternalDwgFile,
                            FilePath = fullPath,
                            DisplayName = $"外部文件: {Path.GetFileName(fullPath)}",
                            PipeLayers = pipeLayers,
                            AttachmentLayers = attrLayers
                        };
                    }
                    break;
            }

            return new DataSourceDescriptor
            {
                Kind = DataSourceKind.Unresolved,
                FilePath = (_surveyExternalFilePath ?? string.Empty).Trim(),
                DisplayName = "未指定有效勘测数据",
                PipeLayers = pipeLayers,
                AttachmentLayers = attrLayers
            };
        }

        private void BrowsePreviousFile()
        {
            string selected = ShowOpenDwgFileDialog("选择【上版数据】DWG 图纸文件", PreviousExternalFilePath);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                PreviousExternalFilePath = selected;
                PreviousSourceKind = DataSourceKind.ExternalDwgFile;
            }
        }

        private void BrowseSurveyFile()
        {
            string selected = ShowOpenDwgFileDialog("选择【勘测数据】DWG 图纸文件", SurveyExternalFilePath);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                SurveyExternalFilePath = selected;
                SurveySourceKind = DataSourceKind.ExternalDwgFile;
            }
        }

        private string ShowOpenDwgFileDialog(string title, string currentPath)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = title,
                    Filter = "AutoCAD 图纸文件 (*.dwg)|*.dwg|所有文件 (*.*)|*.*",
                    CheckFileExists = true
                };

                if (!string.IsNullOrWhiteSpace(currentPath))
                {
                    string dir = Path.GetDirectoryName(currentPath);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    {
                        dlg.InitialDirectory = dir;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(PlanningFilePath))
                {
                    string dir = Path.GetDirectoryName(PlanningFilePath);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    {
                        dlg.InitialDirectory = dir;
                    }
                }

                if (dlg.ShowDialog() == true)
                {
                    return dlg.FileName;
                }
            }
            catch
            {
                // 忽略对话框异常
            }
            return null;
        }

        /// <summary>
        /// 确认当前数据源选择，更新会话 <see cref="PipeDatasetContext"/> 并持久化写入 Config.json。
        /// </summary>
        public bool ConfirmAndSave()
        {
            DataSourceDescriptor newPrev = BuildPreviousDescriptor();
            DataSourceDescriptor newSurvey = BuildSurveyDescriptor();

            if (_datasetContext != null)
            {
                string oldPrevKey = _datasetContext.PreviousDataSource?.GetCacheKey() ?? string.Empty;
                string newPrevKey = newPrev.GetCacheKey();
                _datasetContext.PreviousDataSource = newPrev;
                if (!string.Equals(oldPrevKey, newPrevKey, StringComparison.OrdinalIgnoreCase))
                {
                    _datasetContext.InvalidatePreviousData();
                }

                _datasetContext.SurveyDataSource = newSurvey;
            }

            bool prefChanged =
                CadConfig.DataSources.PreferSameDrawingLayers != PreferSameDrawingLayers ||
                CadConfig.DataSources.PreferOpenMdiDocuments != PreferOpenMdiDocuments;

            CadConfig.DataSources.PreferSameDrawingLayers = PreferSameDrawingLayers;
            CadConfig.DataSources.PreferOpenMdiDocuments = PreferOpenMdiDocuments;

            _dataSourceService.RememberDataSourcePaths(newPrev, newSurvey, saveImmediately: false);
            if (prefChanged || newPrev.IsValid || newSurvey.IsValid)
            {
                CadConfig.Save();
            }

            DialogConfirmed = true;
            FooterStatusText = "已应用数据源设置并同步保存至 Config.json";
            RefreshValidationSummary();
            RequestClose?.Invoke(true);
            return true;
        }

        public void Cancel()
        {
            DialogConfirmed = false;
            RequestClose?.Invoke(false);
        }

        private void RefreshValidationSummary()
        {
            OnPropertyChanged(nameof(IsPreviousSourceValid));
            OnPropertyChanged(nameof(PreviousValidationText));
            OnPropertyChanged(nameof(IsSurveySourceValid));
            OnPropertyChanged(nameof(SurveyValidationText));
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
