# ProcOpt

**Windows 进程调优工具** — 按规则自动管理进程的 CPU 亲和性、优先级与能效模式，并实时监控系统状态。

基于 .NET 8 / WPF 构建，深色现代界面，常驻系统托盘，开箱即用。

## 功能特性

### 规则自动化
- 按进程名创建规则，自动设置 **CPU 亲和性 / 优先级 / 能效模式**（Windows 11 Efficiency Mode）
- 后台轮询检测新启动的进程并自动应用规则，支持一键应用到已在运行的进程
- 应用成功后托盘气泡通知

### 系统监控
- CPU 型号与 P/E 核心拓扑可视化
- CPU / GPU 负载与温度实时监控（LibreHardwareMonitor + PawnIO 驱动）
- 内存占用实时曲线图

### 进程管理
- 浏览全部运行中进程，手动调整优先级、能效模式与 CPU 亲和性
- 可视化亲和性编辑器，按 P/E 核分组选择

### 内存清理
- 一键整理全部进程工作集并清空系统备用列表
- 按内存占用阈值与时间间隔自动清理

### 其他
- 托盘常驻、开机静默自启（`--tray` 参数）
- 单实例运行，重复启动自动唤起主窗口
- 完整的应用日志，记录每次规则应用
- 启动时自动检查新版本

## 环境要求

- Windows 10 (1809+) / Windows 11，x64
- 管理员权限（硬件监控与驱动访问需要，程序启动时自动请求 UAC 提升）
- 首次运行自动检测并静默安装 [PawnIO](https://github.com/namazso/PawnIO) 驱动框架（用于内核级温度/电压读取）

## 下载

前往 [Releases](https://github.com/866899/ProcOpt/releases) 页面下载最新版本，解压后运行 `ProcOpt.exe`。

## 技术栈

| 组件 | 说明 |
|---|---|
| .NET 8 + WPF | MVVM 架构（自研 `Infrastructure` 基础库） |
| LibreHardwareMonitorLib 0.9.6 | CPU / GPU / 内存 / 温度监控 |
| PawnIO | 内核级硬件访问驱动（替代已被微软列入阻止名单的 WinRing0） |
| System.Management | WMI 系统信息查询 |

## 项目结构

```
ProcOpt/
├── App.xaml(.cs)        # 应用入口：单实例互斥、托盘初始化、服务装配
├── Infrastructure/      # MVVM 基础设施（ObservableObject / RelayCommand）
├── Models/              # 数据模型（规则、设置、进程视图、日志条目）
├── Services/            # 业务服务
│   ├── ProcessService    # 进程操作（优先级 / 能效 / 亲和性）
│   ├── RuleEngine        # 规则自动应用引擎（轮询 + 匹配）
│   ├── RulesStore        # 规则持久化
│   ├── TemperatureService / GpuService / CpuTopology / SystemInfoService
│   ├── MemoryCleanService # 内存清理（手动 + 阈值自动）
│   ├── TrayIconService   # 托盘图标与菜单
│   ├── AutoStartService  # 开机自启（计划任务）
│   └── UpdateService     # 版本检查与更新
├── ViewModels/          # 视图模型（Main / Process / Rules / System / Log / Settings）
└── Views/               # 界面
    ├── MainWindow.xaml   # 主窗口（左侧导航 + 状态栏）
    ├── Pages/            # 系统信息 / 进程管理 / 规则 / 日志 / 设置
    ├── AffinityWindow    # 亲和性可视化编辑
    └── RuleEditWindow    # 规则编辑
```

## 从源码构建

```bash
git clone https://github.com/866899/ProcOpt.git
cd ProcOpt
dotnet build -c Release
```

需要 .NET 8 SDK（`net8.0-windows` 目标框架）。

## 许可证

TBD
