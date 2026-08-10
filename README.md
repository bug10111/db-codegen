# DbCodeGen 数据库驱动代码生成器

借鉴 easycode 思路的数据库驱动代码生成器桌面工具(v1):数据源 → 读元数据 → 选表 → 选模板包 → 渲染 → dry-run 预览 → 写盘。

## 核心能力

- 多数据源:MySQL / PostgreSQL,读取 information_schema 元数据(表/列/注释/主键/自增/索引/默认值)。
- 多套模板系统:模板包 = 文件夹 + `template.json`(manifest),模板文件使用 Scriban 语法,天然可被 AI 或任何编辑器修改。
- 勾选到层:每个模板文件独立 checkbox,可控制只生成到 service 层。
- 模板编辑器 + 实时预览:AvalonEdit 编辑模板,右侧选表渲染真实代码;变量面板展示表元数据并支持点击插入。
- dry-run 安全写盘:批量写盘前预览新增/覆盖/跳过清单,覆盖前确认。
- AI 模板生成:OpenAI 兼容协议(默认阿里云百炼 DashScope 兼容端点),输入技术栈描述 + 样例表元数据 + TEMPLATE_SPEC 生成整套模板包。
- 数据源密码使用 Windows DPAPI 加密持久化。

## 工程结构

```
DbCodeGen.sln
├─ src/DbCodeGen.Core      核心类库:模型/配置/数据源/模板引擎/生成服务/AI
├─ src/DbCodeGen.App       WPF 桌面壳(CommunityToolkit.Mvvm + AvalonEdit)
└─ test/DbCodeGen.Core.Tests  xUnit 单元测试
```

详细规范见 `src/DbCodeGen.Core/Resources/TEMPLATE_SPEC.md`（内嵌进 `DbCodeGen.Core.dll` 供 AI 模板生成/修改使用）。

## 构建

```bash
dotnet restore
dotnet build
dotnet test
```
