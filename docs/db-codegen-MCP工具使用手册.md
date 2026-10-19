# db-codegen MCP 工具使用手册

> 适用对象：通过 MCP（Model Context Protocol）stdio 传输接入 DbCodeGen 服务端的 AI 客户端（智能体）的使用者与配置者。本文回答四个问题：每个工具**需要什么参数、返回什么、参数/字段什么含义、什么时候用怎么用**，以及整体上**这些工具该按什么顺序用**。
>
> 事实来源：全部参数与必填性取自服务端 `tools/list` 实测 inputSchema；全部返回字段取自 `src/DbCodeGen.McpServer/Tools/*.cs` 的应答 DTO 源码；行为约束与 README「MCP 服务端」章节同源。

## 1. 定位与适用边界

DbCodeGen MCP 服务端（`src/DbCodeGen.McpServer`，.NET 8 控制台，官方 SDK ModelContextProtocol 2.2.0）是一个**本地 stdio MCP 服务端**：AI 客户端以子进程方式拉起它，即可用自然语言驱动全部代码生成能力——查表结构、管数据源、改模板、预览渲染、批量生成。

- **服务端本身不做任何 LLM 调用**：智能由 MCP 客户端侧的大模型承担，服务端只暴露工具面（共 19 个工具）。
- **与桌面端共享数据**：与 WPF 桌面端共用同一份 `%AppData%\DbCodeGen\config.json` 与用户模板库（桌面端经 `config-location.json` 重定向数据目录时共用重定向后目录）。config 读写与模板包操作均经跨进程命名 Mutex 串行化，双开不会互相写坏文件。
- **同机同用户**：数据源密码经 Windows DPAPI（CurrentUser 作用域）加密保存，仅同机同 Windows 用户可解密；stdio 子进程形态天然同机，不提供远程形态。
- **零记忆态副作用**：MCP 调用一切参数显式传递（表、模板选择、覆盖策略、路径），服务端不读取桌面端的勾选态/最近目录等 UI 记忆，生成后也不回写 config。
- 注册方法、构建发布命令见 README「MCP 服务端」章节与本文第 7 节速查。

## 2. 工具总览

25 个工具按六层能力分类。除「模板编辑」「模板包管理」「代码生成」「数据源管理」外全部只读。

| 分类 | 工具 | 一句话用途 | 读写性 |
| --- | --- | --- | --- |
| 只读枚举（8） | `list_template_packages` | 列出全部模板包（内置 + 用户），含只读标记、文件数与能力元数据（role/stack/scopes/hasBackendView/hasWebPage/contractPath/deployModes，契约缺失回执 MissingContract 告警） | 只读 |
| | `list_templates` | 列出指定包内模板条目，可按分组/语言过滤 | 只读 |
| | `read_template` | 读取模板文件正文全文（Scriban 源文本） | 只读 |
| | `list_datasources` | 列出已保存的数据源连接（密码永不回显） | 只读 |
| | `list_tables` | 列出指定数据源下全部表（不含列） | 只读（连库） |
| | `get_table_detail` | 读取指定表完整列元数据 | 只读（连库） |
| | `get_type_map` | 查看生效的类型映射表（全局 > 包 typeMap > 兜底） | 只读 |
| | `check_type_mapping` | 生成前预检未映射的数据库类型 | 只读（连库） |
| 模板编辑（5） | `save_template_content` | 覆写保存用户包内既有模板正文 | 写（模板） |
| | `save_template_edit` | 子串级单处精确替换（oldText 唯一命中 + 可选 contentHash 条件写） | 写（模板） |
| | `create_template_file` | 向用户包新增空模板文件（可含分组目录） | 写（模板） |
| | `delete_template_file` | 删除用户包内模板文件（物理删除，不可恢复） | 写（模板） |
| | `rename_template_file` | 重命名/移动用户包内模板文件 | 写（模板） |
| 模板包管理（5） | `create_template_package` | 新建用户模板包（可设说明/基础包名） | 写（模板） |
| | `copy_template_package` | 复制模板包为新用户包（内置包可复制后修改） | 写（模板） |
| | `rename_template_package` | 重命名用户模板包（勾选记忆不迁移） | 写（模板） |
| | `update_template_package_metadata` | 更新包说明/基础包名/包级类型映射 | 写（模板） |
| | `delete_template_package` | 删除用户模板包（confirm=true，不可恢复） | 写（模板） |
| 预览（2） | `render_template` | 以合成表上下文渲染单个模板（无需连库） | 只读 |
| | `preview_generation` | 批量生成 dry-run 预览（零写盘） | 只读 |
| 代码生成（1） | `generate` | 执行批量生成并写盘（内部先重算 dry-run） | **写（代码文件）** |
| 数据源管理（4） | `add_datasource` | 新增数据源连接（密码 DPAPI 加密入库） | 写（配置） |
| | `update_datasource` | 按名更新连接（只传要改的字段） | 写（配置） |
| | `delete_datasource` | 按名删除连接 | 写（配置） |
| | `test_connection` | 测试连通性：按名测试，或完整明文直测（不入库） | 只读（连库） |

## 3. 典型使用顺序（推荐管线）

### 3.1 流A：首次接入探活

1. 客户端自动完成 `initialize` 握手与 `tools/list`（stdio 注册后由 MCP 协议层自动执行，无需手动调用工具）。服务端标识：name=`DbCodeGen`。
2. `list_template_packages`：摸底有哪些包可用。**内置模板包已移除**：当前发布不含任何内置模板包，模板包全部来自用户模板库（用户包可编辑）；若部署侧自行放入 `Templates\Builtin` 的内置包，其 `isBuiltin=true`（**只读**，编辑类工具一律拒绝）。返回的 `fileCount` 与 `basePackage` 可辅助选择。
3. `list_templates`（可带 `type`/`group` 过滤）浏览包内条目；`read_template` 看正文确认模板风格。

跳过第 2 步的后果：直接对不存在的包名调用后续工具会得到 `TemplatePackageError`（包不存在）。

### 3.2 流B：数据源接入

```
add_datasource → test_connection(datasource=按名) → list_tables → get_table_detail
```

1. `test_connection` **明文直测**（传 `type/host/port/database/userId`，不传 `datasource`）：先验证连接参数可用，直测不入库不留痕。
2. `add_datasource`：确认可用后落库保存（密码经 DPAPI 加密入库）。重名会得到 `NameConflict`。
3. `test_connection`（传 `datasource=名称`）：验证已保存连接（使用 DPAPI 密文凭据）可连通。
4. `list_tables` → `get_table_detail`：确认库内表与列元数据，为生成流水线备料。

顺序理由：先直测再落库，避免把不可用参数写进配置；保存后再按名测一次，闭环验证密文凭据解密与连接串组装。直接 `add_datasource` 而不先直测的后果：坏参数入库后每次生成链路都要排障，且需再调 `update_datasource` 修正。

### 3.3 流C：代码生成主流水线（核心）

```
get_table_detail → check_type_mapping → list_templates → read_template
→ render_template → preview_generation → generate
```

1. `list_datasources` / `list_tables` / `get_table_detail`：取得表列元数据。`get_table_detail` 返回的列条目可直接映射为生成入参的合成列（`name/comment/isPrimaryKey/autoIncrement/isNullable/length/precision/scale` 同名直传；`dbType` 改名 `rawDbType`；`propertyName` 是服务端派生字段，不传）。
2. `check_type_mapping`：**生成前预检**。对同样的 `datasource + tables + package` 组合按生效映射链（数据库专属条目 > 通用条目 > 包 typeMap）检查未映射类型。`allMapped=false` 时返回的 `unmapped` 清单给出每种未映射类型的首次出现位置与总次数。
   - **跳过的后果**：未映射类型的列生成时回落为兜底类型 `String`（见 `get_type_map` 的 `defaultType`），产出代码里该列字段不是期望类型（如该 `Long` 的列变 `String`），只能返工重生成。
   - 预检与生成必须用**同一个包**（包级 typeMap 口径一致）。
3. `list_templates`（可用 `files` 视角预选）：确认包内条目路径、输出路径模板与 `enabled` 标记；`read_template` 需要时核对正文。
4. `render_template`：单模板试渲染。用合成表上下文（无需连库）验证模板语法与渲染结果，语法错误精确到行列。
5. `preview_generation`：dry-run 出文件计划——表 × 模板笛卡尔积、每条目标路径与 `new/overwrite/skip` 分类、未映射类型清单。**零写盘**，确认前零风险。
6. `generate`：确认文件计划后落盘。`generate` 内部会强制重算 dry-run 再写盘，**无确认弹窗**，所以第 5 步 preview 是唯一的人工/智能体把关点。

参数约定（preview/generate 共用同一入参口径）：`package` 必填；`tables` 按所选模板分型——所选模板（`files` 过滤后）含 table 作用域条目时必填非空（内联合成表，至少一张，需要完整列时用第 1 步取回的元数据），纯 module/project 作用域集合可省略；`files` 省略时默认选择包内**全部启用条目**（`enabled=false` 的条目不参与）；`workspaceRoot`/`codeDirectory` 指定输出位置；`webDirectory` 指定 root=web 模板的前端锚点（缺省 `web`，必须相对且不含 `..`）；`includePlatformTables=true` 显式允许生成平台表（缺省 false 时命中模板包 `platformTables` 规则的表排除并在 `platformTableExcluded` 列出，排除表规则不受该开关影响）；`duplicateStrategy` 控制 `overwrite`（内容不同才覆盖）/`skip`（同名一律跳过），**MCP 缺省 `skip`**（桌面端 App 缺省仍为 `overwrite`）。

### 3.4 流D：模板维护

```
list_templates → read_template → 编辑（四选一）→ render_template 回归验证
```

- 改既有模板：`read_template` 取当前正文 → `save_template_content` 整篇覆写（文件必须已存在）。
- 增新模板：`create_template_file`（建空文件 + 清单条目，`outputPath` 必填）→ `save_template_content` 写正文。
- 删模板：`delete_template_file`（**物理删除，不可恢复**；允许删到包变空包）。
- 重命名/移动分组：`rename_template_file`（`newPath` 可含新分组目录；桌面端的勾选记忆不迁移）。
- 每次编辑后用 `render_template` 以同一合成表回归验证，再进流C。

**注意**：桌面端不监听模板文件变更——MCP 侧修改模板后，桌面端需重新加载包（重开「模板包管理」窗口或重启应用）才能看到最新内容；反向同理，MCP 服务端每次工具调用即时读盘，无缓存陈旧问题。四个编辑工具对内置包一律拒绝（`TemplatePackageError`）。

### 3.5 流E：连接直测（排障）

`test_connection` 明文直测不入库的模式，适用于：配置前的参数验证、怀疑已保存连接参数过期时的快速验证（不经 DPAPI 解密路径）、任何「不想留痕」的连通性探测。直测模式密码仅瞬态参与连接组装，不进入持久化、应答与日志。

## 4. 工具参考（逐工具）

通用约定（适用于本节全部工具）：

- 参数名与类型 = 服务端 `tools/list` 实测 inputSchema，逐字段一致；各参数表「必填」列标注必填性。
- 返回 JSON 字段名为小驼峰（camelCase）；**值为 null 的字段在应答中省略不输出**。
- 失败应答形态与识别口径见第 5 节「错误面契约」。
- 模板「分组」= 包内模板相对路径的子目录前缀；「类型/语言」= 模板文件名 `<名称>.<语言段>.scriban` 的语言段（`HighlightLanguage`：Java/CSharp/Xml/Sql/Json/Plain）；「模块」= `codeDirectory` 的 basePackage 末段（无独立实体，由代码目录解析派生）。

### 合成表与合成列（`tables[]` / `table` / `column` 入参结构）

`render_template` 的 `table`/`column` 与 `preview_generation`/`generate` 的 `tables[]` 使用同一套合成表模型，**无需连接真实数据库**：

`tables[]`（对象）字段：

| 字段 | 类型 | 必填 | 含义 |
| --- | --- | --- | --- |
| `name` | string | 是（运行时校验非空） | 原始表名，与数据库一致（如 `t_user_info`）；类名/变量名由服务端按原始名自动派生 |
| `schemaName` | string | 否 | 所属 schema/库名，跨库场景使用 |
| `comment` | string | 否 | 表注释 |
| `columns` | array | 否 | 列元数据集合；**省略时表按「仅表壳无列」参与渲染**（实体类等按列循环的模板将输出空列清单）；需要完整列时建议先用 `get_table_detail` 取回后传入 |

`columns[]`（对象）字段：

| 字段 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `name` | string | 是（运行时校验非空） | — | 原始列名（如 `user_name`）；属性名由服务端按 camelCase 自动派生 |
| `rawDbType` | string | 否 | `""` | 原始数据库类型（如 `varchar`、`bigint`），**不含长度修饰** |
| `comment` | string | 否 | null | 列注释 |
| `isPrimaryKey` | boolean | 否 | false | 是否主键列；联合主键的每一列均传 true |
| `autoIncrement` | boolean | 否 | false | 是否自增列 |
| `isNullable` | boolean | 否 | false | 是否可空；false 表示非空列 |
| `defaultValue` | string | 否 | null | 列默认值 |
| `length` | integer | 否 | null | 列长度（仅字符等类型有值） |
| `precision` | integer | 否 | null | 精度（numeric/decimal 等类型有值） |
| `scale` | integer | 否 | null | 小数位（numeric/decimal 等类型有值） |

`render_template` 的顶层 `column` 参数（列级模板的当前列上下文）结构同 `columns[]`；其 schema 中 `name`/`rawDbType` 标注了可空（SDK 对可空参数对象的生成形态），但**运行时校验 `name` 必须非空**。

### 4.1 `list_template_packages`

**用途与使用时机**：列出全部可用模板包（内置包 + 用户包），生成与模板维护前选包的第一步。何时不用：已知包名且只需包内条目时直接 `list_templates`。

参数：无。

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `packages` | array | 模板包摘要列表 |
| `packages[].name` | string | 包名（唯一，= 目录名） |
| `packages[].description` | string | 包说明 |
| `packages[].engine` | string | 模板引擎，当前固定 `scriban` |
| `packages[].basePackage` | string \| 省略 | 基础包名，未配置时省略 |
| `packages[].isBuiltin` | boolean | 是否内置包（只读，不可编辑删除） |
| `packages[].fileCount` | integer | 包内模板文件数 |
| `packages[].modifiedTime` | string | 包目录最近修改时间（ISO 8601 本地时区） |
| `packages[].role` | string | 包角色：`backend` / `scaffold` / `web` / `fullstack` / `utility`（manifest 声明优先，缺省按产物推断） |
| `packages[].stack` | string | 技术栈：`java` / `net` / `web` / `mixed` |
| `packages[].scopes` | array of string | 文件作用域集合（`table` / `module` / `project`，顺序固定） |
| `packages[].hasBackendView` | boolean | 是否具备后端 View 层产出 |
| `packages[].hasWebPage` | boolean | 是否具备前端业务页面产出 |
| `packages[].contractPath` | string | 契约文档相对包根路径（缺省 `README.md.scriban`） |
| `packages[].deployModes` | array of string | 支持的部署形态（如 `standalone` / `cloud`） |
| `packages[].hasContract` | boolean | 契约文档是否实际存在 |
| `packages[].warnings` | array of string | 能力/契约告警（如 `MissingContract：…`，非阻断） |

**错误态**：`InternalError`（包枚举底层异常，罕见）。

**调用示例**：

```json
{}
```

### 4.2 `list_templates`

**用途与使用时机**：浏览指定包内的模板条目（相对路径、输出路径模板、启用标记、语言），生成前确定 `files` 候选、模板维护前定位目标文件。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `package` | string | 是 | — | 模板包包名 |
| `group` | string | 否 | null | 分组前缀过滤：匹配模板相对路径的子目录前缀（目录边界对齐，`controller` 不会误命中 `controllerx/`）；多级子目录用正斜杠分隔；大小写不敏感 |
| `type` | string | 否 | null | 语言段过滤，取值 `java` / `csharp`（或 `cs`）/ `xml` / `sql` / `json` / `plain`，大小写不敏感；非法取值被拒绝并列出合法值 |

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `package` | string | 包名 |
| `isBuiltin` | boolean | 是否内置包 |
| `basePackage` | string \| 省略 | 基础包名，未配置时省略 |
| `templates` | array | 过滤后命中的模板条目列表 |
| `templates[].path` | string | 模板相对包根路径（可含分组目录；后续 `read_template`/渲染/编辑类工具的 `path` 都用它） |
| `templates[].output` | string | 输出相对路径模板，支持 `{{变量}}` 占位 |
| `templates[].enabled` | boolean | 是否默认参与生成（`files` 省略时的选择依据） |
| `templates[].language` | string | 按文件名语言段推导的目标语言（Java/CSharp/Xml/Sql/Json/Plain） |
| `templates[].scope` | string | 模板作用域：`table`（缺省）/ `module` / `project` |
| `templates[].root` | string | 输出根语义：`legacy`（未声明，按作用域沿用既有语义）/ `output` / `workspace` / `web` |
| `templates[].role` | string | 产出角色（manifest 声明优先，缺省按输出路径推断，如 `backendView` / `webPage`） |
| `templates[].isContract` | boolean | 是否包契约文档条目 |
| `warnings` | array of string | 契约缺失等非阻断告警（`MissingContract：…`） |

**包契约文档型条目**：当包根存在 `manifest.contractPath`（缺省 `README.md.scriban`）且未在 `template.json files[]` 声明时，`templates` 末尾附带一条合成条目（`path` 为契约路径、`output` 为空串、`enabled=false`、`scope=project`、`isContract=true`）。该条目不在清单中、不计入 `fileCount`、不参与生成；`group` 非空时不返回，`type` 过滤按契约文件名的语言段判定。

**错误态**：`TemplatePackageError`（包不存在）；`InvalidArgument`（`type` 非法）。

**调用示例**：

```json
{ "package": "my-package", "group": "controller", "type": "java" }
```

### 4.3 `read_template`

**用途与使用时机**：读取模板文件正文全文（Scriban 源文本），改模板前取当前内容、评估模板风格时使用。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `package` | string | 是 | — | 模板包包名 |
| `path` | string | 是 | — | 模板相对包根路径（`list_templates` 返回的 `path`；反斜杠会规范化为正斜杠比对） |

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `package` | string | 包名 |
| `path` | string | 模板相对包根路径 |
| `output` | string | 输出相对路径模板 |
| `enabled` | boolean | 是否默认参与生成 |
| `language` | string | 目标语言 |
| `content` | string | 模板正文全文（UTF-8 去 BOM 解码） |
| `contentHash` | string | 正文内容摘要（SHA256 前 16 位 hex 小写），供 `save_template_edit` 条件写期望值 |

**包契约文档窄例外**：`manifest.contractPath`（缺省包根 `README.md.scriban`）即使未在 `template.json files[]` 声明也可读取；返回文档型元数据（`output` 为空串、`enabled=false`），读取仍经防目录穿越校验。契约路径之外的子目录形态、父级跳转形态、绝对路径/盘符形态与其它未声明文件一律拒绝；契约缺失时回执 `MissingContract` 告警（非阻断）。

**错误态**：`TemplatePackageError`（包不存在；`path` 未在包清单声明——只允许读清单内模板文件或 `manifest.contractPath` 窄例外，杜绝任意文件读取；契约文件不存在同样报此错误）。

**调用示例**：

```json
{ "package": "my-package", "path": "entity.java.scriban" }
```

### 4.4 `list_datasources`

**用途与使用时机**：列出全部已保存的数据源连接摘要，为 `list_tables`/`get_table_detail`/`check_type_mapping`/按名 `test_connection` 提供 `datasource` 名称。

参数：无。

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `datasources` | array | 连接摘要列表 |
| `datasources[].name` | string | 连接名称，全局唯一 |
| `datasources[].type` | string | 数据库类型：`MySql` / `PostgreSql` |
| `datasources[].host` | string | 主机名或 IP |
| `datasources[].port` | integer | 端口 |
| `datasources[].database` | string | 数据库名 |
| `datasources[].userId` | string | 用户名 |
| `datasources[].createdAt` | string | 创建时间（ISO 8601 本地时区） |
| `datasources[].updatedAt` | string | 最近更新时间 |

**强制**：密码明文与 DPAPI 密文不设任何应答字段，结构性不回显。

**错误态**：`InternalError`（配置读取异常，罕见）。

**调用示例**：

```json
{}
```

### 4.5 `list_tables`

**用途与使用时机**：列出指定数据源下全部表（不含列元数据），生成前圈定表集合。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `datasource` | string | 是 | — | 已保存的数据源连接名（`list_datasources` 可查） |

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `datasource` | string | 数据源连接名 |
| `tables` | array | 表摘要列表 |
| `tables[].name` | string | 原始表名，与数据库一致 |
| `tables[].schema` | string \| 省略 | 所属 schema/库名，数据库未提供时省略 |
| `tables[].className` | string | 按表名推导的 PascalCase 类名 |
| `tables[].variableName` | string | 按表名推导的 camelCase 变量名 |
| `tables[].comment` | string \| 省略 | 表注释，无注释时省略 |

**错误态**：`InvalidArgument`（数据源不存在，错误消息附可用连接名清单）；`Timeout`（连接 10 秒兜底超时）；`InternalError`（其余，消息经 Core 层脱敏）。

**调用示例**：

```json
{ "datasource": "虚拟机" }
```

### 4.6 `get_table_detail`

**用途与使用时机**：读取指定表完整列元数据，生成主流水线第一步（合成表列数据的权威来源）、类型预检前的核对依据。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `datasource` | string | 是 | — | 已保存的数据源连接名 |
| `table` | string | 是 | — | 目标表名（`list_tables` 返回的 `name`） |

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `datasource` | string | 数据源连接名 |
| `name` | string | 原始表名 |
| `schema` | string \| 省略 | 所属 schema/库名 |
| `className` | string | 类名 |
| `variableName` | string | 变量名 |
| `comment` | string \| 省略 | 表注释 |
| `columns` | array | 完整列元数据（字段结构见下） |
| `primaryKeyColumns` | array of string | 主键列名清单（联合主键含多列），便于快速核对 |

`columns[]` 字段（可直接映射为生成入参的合成列）：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `name` | string | 原始列名 |
| `propertyName` | string | 驼峰属性名（服务端派生；作为合成列入参时不传，服务端会自行派生） |
| `dbType` | string | 原始数据库类型（不含长度修饰）——映射为合成列的 `rawDbType` |
| `comment` | string \| 省略 | 列注释 |
| `isPrimaryKey` | boolean | 是否主键列（联合主键每列均为 true） |
| `autoIncrement` | boolean | 是否自增列 |
| `isNullable` | boolean | 是否可空 |
| `length` | integer \| 省略 | 列长度，无长度语义时省略 |
| `precision` | integer \| 省略 | 精度，无精度语义时省略 |
| `scale` | integer \| 省略 | 小数位，无精度语义时省略 |

**错误态**：`InvalidArgument`（数据源不存在；表不存在）；`Timeout`；`InternalError`。

**调用示例**：

```json
{ "datasource": "虚拟机", "table": "t_user_info" }
```

### 4.7 `get_type_map`

**用途与使用时机**：查看生效的类型映射表，了解当前映射口径；生成前排查「为什么这列映射成了 String」时配合 `check_type_mapping` 使用。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `package` | string | 否 | null | 模板包包名；提供时一并返回该包清单声明的包级 typeMap |

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `global` | array | 全局映射条目列表（配置存储，实时反映桌面端或 MCP 侧最新维护结果） |
| `global[].dbType` | string | 数据库原始类型 |
| `global[].targetType` | string | 目标语言类型 |
| `global[].import` | string \| 省略 | 可选导包，无导包需求时省略 |
| `global[].remark` | string \| 省略 | 备注 |
| `global[].databaseType` | string \| 省略 | 适用数据库类型（`MySql`/`PostgreSql`）；省略表示通用条目（对所有数据库生效） |
| `package` | string | 包名；未指定包时为空串 |
| `packageTypeMap` | array \| 省略 | 包级 typeMap 条目（`dbType`/`targetType`）；未指定包时省略 |
| `defaultType` | string | 全部未命中时的兜底目标类型，固定 `String` |

生效优先级：数据库专属条目 > 通用条目 > 包 typeMap > 兜底 `String`。

**错误态**：`TemplatePackageError`（指定包不存在）；`InternalError`。

**调用示例**：

```json
{ "package": "my-package" }
```

### 4.8 `check_type_mapping`

**用途与使用时机**：生成前预检未映射类型，**必须在 `generate` 之前调用**（与生成同参数口径：同 `datasource`、同 `tables`、同 `package`）。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `datasource` | string | 是 | — | 已保存的数据源连接名 |
| `tables` | array of string | 是 | — | 待生成的表名清单（`list_tables` 返回的 `name`）；空集合被拒绝 |
| `package` | string | 是 | — | 目标模板包包名（预检与生成须同一包，保证 typeMap 口径一致） |

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `datasource` | string | 数据源连接名 |
| `package` | string | 模板包包名 |
| `tableCount` | integer | 参与预检的表数量 |
| `allMapped` | boolean | 是否全部列类型均命中映射链（true 才建议直接进入生成） |
| `unmapped` | array | 未映射类型清单（按类型名排序） |
| `unmapped[].dbType` | string | 未映射的数据库原始类型 |
| `unmapped[].tableName` | string | 首次出现的表名 |
| `unmapped[].columnName` | string | 首次出现的列名 |
| `unmapped[].occurrences` | integer | 跨表跨列总出现次数 |

**错误态**：`InvalidArgument`（数据源不存在；`tables` 为空）；`TemplatePackageError`（包不存在）；`Timeout`；`InternalError`。

**调用示例**：

```json
{ "datasource": "虚拟机", "tables": ["t_user_info"], "package": "my-package" }
```

### 4.9 `save_template_content`

**用途与使用时机**：覆写保存用户包内**既有**模板文件的正文（流D「改模板」的落盘动作）。新建文件用 `create_template_file`；内置包一律拒绝。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `package` | string | 是 | — | 目标用户模板包包名 |
| `path` | string | 是 | — | 模板文件相对包根路径（须已存在于包清单） |
| `content` | string | 是 | — | 模板正文全文（Scriban 源文本；UTF-8 无 BOM 落盘） |

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `package` | string | 包名 |
| `action` | string | 固定 `saved` |
| `path` | string | 已保存的模板相对路径（正斜杠形态） |
| `newPath` | string \| 省略 | 仅 `renamed` 动作有值（新路径）；保存/新增/删除动作恒省略 |
| `fileCount` | integer | 操作完成后包内模板文件数 |

**错误态**：`TemplatePackageError`（内置包只读；包不存在；同名冲突）；`InvalidArgument`（路径穿越等校验失败；文件不存在）；`InternalError`。

**调用示例**：

```json
{ "package": "my-templates", "path": "entity.java.scriban", "content": "package {{package.basePackage}}.entity;\n..." }
```

### 4.10 `create_template_file`

**用途与使用时机**：向用户包新增一个空模板文件（建空文件并追加清单条目，`enabled=true`），正文随后用 `save_template_content` 写入。`path` 含子目录时即自动创建「分组」。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `package` | string | 是 | — | 目标用户模板包包名 |
| `path` | string | 是 | — | 新增模板文件相对包根路径，可含分组目录（如 `controller/user.java.scriban`） |
| `outputPath` | string | 是 | — | 输出相对路径模板，支持 `{{变量}}` 占位（如 `src/{{table.variableName}}/User.java`） |

**返回**：同 4.9 结构，`action` 固定 `created`。

**错误态**：`TemplatePackageError`（内置包只读；同名文件已存在；包不存在）；`InvalidArgument`（`outputPath` 为空；路径穿越/绝对路径/盘符前缀等校验失败）；`InternalError`。

**调用示例**：

```json
{ "package": "my-templates", "path": "controller/user.java.scriban", "outputPath": "{{package.dir}}/controller/{{table.className}}Controller.java" }
```

### 4.11 `delete_template_file`

**用途与使用时机**：从用户包删除一个模板文件（物理删除文件并同步移除清单条目）。**不可恢复**，调用前确认；允许删除最后一个模板（包变空包）。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `package` | string | 是 | — | 目标用户模板包包名 |
| `path` | string | 是 | — | 待删除模板文件相对包根路径 |

**返回**：同 4.9 结构，`action` 固定 `deleted`。

**错误态**：`TemplatePackageError`（内置包只读；包不存在）；`InvalidArgument`（路径校验失败；文件不存在）；`InternalError`。

**调用示例**：

```json
{ "package": "my-templates", "path": "controller/user.java.scriban" }
```

### 4.12 `rename_template_file`

**用途与使用时机**：重命名/移动用户包内模板文件（物理改名并同步更新清单条目）。`newPath` 可含新分组目录（移动分组）；桌面端对该模板的勾选记忆不迁移。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `package` | string | 是 | — | 目标用户模板包包名 |
| `path` | string | 是 | — | 当前模板文件相对包根路径 |
| `newPath` | string | 是 | — | 新模板文件相对包根路径，可含新分组目录 |

**返回**：同 4.9 结构，`action` 固定 `renamed`；`path` 为旧路径，`newPath` 为新路径（正斜杠形态）。

**错误态**：`TemplatePackageError`（内置包只读；newPath 与既有文件同名冲突；包不存在）；`InvalidArgument`（`newPath` 为空；路径校验失败；源文件不存在）；`InternalError`。

**调用示例**：

```json
{ "package": "my-templates", "path": "entity.java.scriban", "newPath": "pojo/entity.java.scriban" }
```

### 4.13 `render_template`

**用途与使用时机**：以合成表上下文渲染单个模板（**无需数据库连接**），模板维护后的回归验证、生成前确认单模板输出。渲染失败按业务结果返回（`success=false` + 行列定位），不算调用错误。支持可选 `variables` / `modules` 作用域片段渲染（module 作用域片段渲染入口），**返回文本不落盘**。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `package` | string | 是 | — | 模板包包名 |
| `path` | string | 是 | — | 模板相对包根路径（`list_templates` 返回的 `path`） |
| `table` | object | 否 | null | 合成表上下文（结构见本章开头「合成表与合成列」）；表名必填，列集合可选。**传入时按表上下文渲染（现状行为）；省略时按作用域片段渲染**（与 `modules` 互斥） |
| `column` | object | 否 | null | 当前列上下文（结构同合成列），供列级模板内 `column` 变量消费；表级模板省略；依赖 `table` 提供 |
| `basePackageOverride` | string | 否 | null | 本次渲染的基础包名覆盖值；缺省使用模板包 manifest 的 `basePackage`（无则为空） |
| `variables` | object | 否 | null | 调用方变量对象，逐键注入模板 `vars.*`（如 `{"appName":"demo"}`）；键与保留命名空间 `table/column/package/tool/vars/module/tables` 冲突时结构化拒绝 |
| `modules` | array of object | 否 | null | 模块清单（如 `[{"name":"account"}]`）；**单次最多一个元素**，取首元素为 module 上下文（`name` 必填，其余字段原样注入 `module.*`）；与 `table` 互斥；省略 `table` 且未传 `modules` 时按 project 型上下文（vars + 空 tables 汇总）渲染 |

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `success` | boolean | 渲染是否成功 |
| `output` | string | 渲染后全文，仅成功时有值 |
| `errorMessage` | string | 结构化错误描述（含模板名与行列），仅失败时有值 |
| `errorLine` | integer \| 省略 | 错误行号（从 1 开始），未知时省略 |
| `errorColumn` | integer \| 省略 | 错误列号（从 1 开始），未知时省略 |
| `renderDurationMs` | integer | 渲染耗时（毫秒） |

**错误态**：`TemplatePackageError`（包不存在；`path` 未在清单声明）；`InvalidArgument`（`package`/`path` 为空；合成表名为空；合成列名为空；`variables` 键与保留命名空间冲突；`modules` 元素缺 `name`/重复/多元素；`table` 与 `modules` 同时提供；省略 `table` 时传 `column`）；`InternalError`。

**调用示例**：

```json
{
  "package": "my-package",
  "path": "entity.java.scriban",
  "table": {
    "name": "t_user_info",
    "comment": "User Info",
    "columns": [
      { "name": "id", "rawDbType": "bigint", "isPrimaryKey": true, "autoIncrement": true, "isNullable": false },
      { "name": "user_name", "rawDbType": "varchar", "isNullable": true, "length": 64 }
    ]
  }
}
```

module 作用域片段渲染示例（模板引用 `{{module.name}}` 与 `{{vars.*}}`，返回文本不落盘）：

```json
{
  "package": "zz-probe-scope",
  "path": "module-info.cs.scriban",
  "variables": { "appName": "demo" },
  "modules": [ { "name": "account" } ]
}
```

### 4.14 `preview_generation`

**用途与使用时机**：批量生成 dry-run 预览，**零写盘**。返回文件计划、未映射类型清单与结构化诊断（`namingReport` / `pathWarnings` / `coverageWarnings` / `platformTableExcluded`），是 `generate` 前的唯一把关点，必须在生成前调用。文件计划按模板作用域三路展开：`table`（缺省）按表 × 模板笛卡尔积；`module` 按调用方 `modules` 清单逐个；`project` 整次渲染一次——三种作用域同次调用可混跑。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `package` | string | 是 | — | 模板包包名 |
| `tables` | array of object | 条件必填 | null | 合成表集合；所选模板（`files` 过滤后）含 table 作用域条目时必填非空（至少一张，结构见本章开头；需要完整列时先用 `get_table_detail` 取回后传入），纯 module/project 作用域集合可省略 |
| `workspaceRoot` | string | 是 | — | 工作区根目录绝对路径（输出文件的根前缀，须已存在或可创建） |
| `codeDirectory` | string | 是 | — | 代码目录：项目内完整相对路径含包名（如 `src/main/java/com/example/demo`）；解析出基础包名与相对输出根，basePackage 末段即「模块」 |
| `basePackageOverride` | string | 否 | null | 基础包名覆盖值；缺省用代码目录派生值 |
| `files` | array of string | 否 | null | 参与生成的模板相对路径清单；**省略时默认选择包内全部启用条目**；清单含包内不存在的路径会被拒绝 |
| `duplicateStrategy` | string | 否 | `"skip"` | 同名目标文件处理策略：`overwrite`（内容相同跳过/不同覆盖）/ `skip`（同名一律跳过）；大小写不敏感，空白或非法取值被拒绝。MCP 缺省 `skip`（桌面端 App 缺省仍为 `overwrite`） |
| `variables` | object | 否 | null | 调用方变量对象，逐键注入模板 `vars.*`（如 `{"appName":"demo"}`）；键与保留命名空间 `table/column/package/tool/vars/module/tables` 冲突时结构化拒绝；命名契约键 `tablePrefixes/stripTablePrefix/singularize/entitySuffix/packageSegmentCase/layerPrefix/permissionModule` 在此透传，优先级高于 `manifest.naming` |
| `modules` | array of object | 否 | null | 模块清单（如 `[{"name":"account"}]`），包内 `scope: "module"` 模板按此逐个渲染；元素 `name` 必填且清单内不得重复，name 之外字段原样注入 `module.*`；**勾选 module 作用域模板但省略本参时结构化报错** |
| `webDirectory` | string | 否 | `"web"` | root=web 模板的前端锚点（相对 `workspaceRoot`）；必须为相对路径、无盘符前缀、不含 `..`，非法即 `InvalidArgument` |
| `includePlatformTables` | boolean | 否 | `false` | true 时平台表参与生成（回执仍列入 `platformTableExcluded` 且 `excluded=false`）；不影响 `excludeTables`（排除表一律不生成） |

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `entries` | array | 文件计划条目（按作用域分组顺序：表×模板笛卡尔积、模块清单逐个、工程模板依次） |
| `entries[].table` | string | 来源上下文名：table 作用域条目为表名；module 作用域条目为模块名；project 作用域条目为空串 |
| `entries[].relativePath` | string | 渲染后路径（`{{变量}}` 占位已解析）；table/module 条目相对输出根，project 条目相对工作区根 |
| `entries[].absolutePath` | string | 目标文件绝对路径（已通过防目录穿越校验） |
| `entries[].action` | string | 动作分类：`new` / `overwrite` / `skip`（语义见下） |
| `newCount` | integer | 新增条目数 |
| `overwriteCount` | integer | 覆盖条目数 |
| `skipCount` | integer | 跳过条目数 |
| `unmappedTypes` | array | 未映射类型清单，全部命中时为空列表 |
| `unmappedTypes[].dbType` | string | 数据库原始类型名 |
| `unmappedTypes[].table` | string | 出现该类型的表名 |
| `unmappedTypes[].column` | string | 出现该类型的列名 |
| `unmappedTypes[].occurrences` | integer | 出现次数 |
| `pathWarnings` | array | 输出路径告警清单（坏路径预检，**非阻断**、不改变文件计划，但生成前必须核对）；无告警时为空列表 |
| `pathWarnings[].kind` | string | 告警种类：`EmptyPackageDir`（输出路径引用 `package.dir` 占位但包段解析为空）/ `ConsecutiveSeparators`（渲染后路径含连续分隔符）/ `EmptyIteration`（模板内 for 循环零迭代信号）/ `InvalidJavaPackageSegment`（Java 包段非法标识符）/ `DuplicatePackageSegment`（src/main/java 之后连续重复包段）/ `NonLowercasePackageSegment`（包段含大写字母）/ `TableNameRoundTripMismatch`（模板用 hump2/3Underline(table.className) 反推物理表名）/ `PermissionKeyMismatch`（显式权限模块与 modules[0].name kebab 形态不一致）/ `WorkspaceRootMayBeAggregate`（workspaceRoot 自身疑似 Maven 聚合根且 codeDirectory 未落入模块目录） |
| `pathWarnings[].template` | string | 首次出现该告警的模板相对路径 |
| `pathWarnings[].samplePath` | string | 首次出现的渲染后路径样例 |
| `pathWarnings[].occurrences` | integer | 该告警在本次生成范围内的总出现次数（同模板同种类跨表归并） |
| `pathWarnings[].message` | string | 人类可读告警说明（中文，含修复指引） |
| `namingReport` | array | 逐表命名报告，仅含实际参与生成的表（被平台表/排除表规则排除的表不出现） |
| `namingReport[].rawName` | string | 数据库真实物理表名（永不变形） |
| `namingReport[].entityName` | string | 派生实体名（PascalCase，可含 entitySuffix） |
| `namingReport[].variableName` | string | 派生变量名（camelCase） |
| `namingReport[].packagePath` | string | 该表业务代码点号形态包路径（包段已小写归一与连续去重） |
| `namingReport[].tableNameInCode` | string | 代码内物理表名（恒等于 rawName） |
| `namingReport[].permissionKey` | string | 权限键模块段（`{permissionKey}:{op}` 的前缀） |
| `coverageWarnings` | array | 生成覆盖告警：`NoBackendView`（含后端但缺后端 View 层）/ `NoWebPage`（包具备前端页面能力但未选页面模板）/ `OnlyBackendGenerated`（仅选后端模板）；无告警时为空列表 |
| `coverageWarnings[].kind` | string | 告警种类 |
| `coverageWarnings[].message` | string | 人类可读告警说明（含处置指引） |
| `platformTableExcluded` | array | 平台表/排除表命中记录；`excluded=false` 表示平台表经 `includePlatformTables=true` 显式放行 |
| `platformTableExcluded[].tableName` | string | 数据库真实物理表名 |
| `platformTableExcluded[].rule` | string | 命中规则描述（`platform:prefix:sys_` / `exclude:exact:xxx`） |
| `platformTableExcluded[].excluded` | boolean | 是否被排除 |

`action` 分类语义：`new` = 目标不存在（将新增写盘）；`overwrite` = 目标存在且内容不同（将覆盖，非 UTF-8 遗留文件按覆盖处理）；`skip` = 目标存在但不写盘——`skip` 策略下同名一律跳过；`overwrite` 策略下内容相同（行尾归一化后比较）也跳过。

**模板作用域与路径基准**：作用域由包清单 `files[].scope` 声明（`table` 缺省 / `module` / `project`）；输出基准由 `files[].root` 声明（未声明=legacy：table/module 相对输出根、project 相对工作区根；`output`=工作区根+相对输出根；`workspace`=工作区根；`web`=工作区根+`webDirectory`，缺省 `web`）。`workspace` / `web` 根直接拒绝 `..` 段；legacy 与 `output` 根允许 `..` 在工作区根内越出相对输出根；三种根解析后的最终路径都必须落在 `workspaceRoot` 内，渲染期产出的绝对路径/盘符前缀一律拒绝。`project` / `module` 模板可用 `{{tables}}` 迭代本次全部表（元素字段与 `table.*` 同构）生成胶水文件（如 AppDbContext 的 DbSet 登记、DI 装配点），杜绝"往既有文件插入"动作。

**错误态**：`TemplatePackageError`（包不存在；模板渲染失败；输出路径越界等生成域错误，消息含模板名行列或被拒路径；勾选 module 作用域模板但未传 `modules`；模块名重复）；`InvalidArgument`（必填参缺失；`tables` 为空——所选模板含 table 作用域条目时；`files` 含包外路径；`duplicateStrategy` 非法；`webDirectory` 非法（绝对路径/盘符/`..`）；`variables` 键与保留命名空间冲突；`modules` 元素缺 `name`）；`InternalError`。

**调用示例**：

```json
{
  "package": "my-package",
  "tables": [
    { "name": "t_user_info", "columns": [
      { "name": "id", "rawDbType": "bigint", "isPrimaryKey": true, "autoIncrement": true, "isNullable": false },
      { "name": "user_name", "rawDbType": "varchar", "isNullable": true, "length": 64 }
    ] }
  ],
  "workspaceRoot": "C:\\work\\demo-project",
  "codeDirectory": "src/main/java/com/example/demo"
}
```

### 4.15 `generate`

**用途与使用时机**：执行批量生成并写盘。内部**强制先重算 dry-run** 再逐文件写盘，**无确认弹窗**——务必先 `preview_generation` 确认文件计划。全参数显式：不读配置记忆态，生成后不回写 config.json 的最近路径。

参数与 `preview_generation` 完全一致（`package` / `workspaceRoot` / `codeDirectory` 必填；`tables` 条件必填——所选模板含 table 作用域条目时非空，纯 module/project 作用域集合可省略；`basePackageOverride` / `files` / `duplicateStrategy`（缺省 `skip`）/ `variables` / `modules` / `webDirectory` / `includePlatformTables` 可选，含义同 4.14），两个阶段共用同一渲染管线与入参口径。

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `generated` | integer | 新增成功写盘文件数 |
| `overwritten` | integer | 覆盖成功文件数 |
| `skipped` | integer | 跳过未写文件数 |
| `failed` | integer | 写盘失败文件数 |
| `isCancelled` | boolean | 是否被取消；取消时部分文件可能已写盘 |
| `logs` | array | 生成日志列表（逐文件结果） |
| `logs[].timestamp` | string | 日志时间（ISO 8601 本地时区） |
| `logs[].level` | string | 日志级别：`info` / `warning` / `error` |
| `logs[].message` | string | 目标文件相对路径与结果描述，不含敏感信息 |
| `namingReport` | array | 逐表命名报告，字段同 `preview_generation`（`rawName` / `entityName` / `variableName` / `packagePath` / `tableNameInCode` / `permissionKey`） |
| `pathWarnings` | array | 预检告警清单，字段与种类同 `preview_generation`（非阻断，写盘回执仍透出） |
| `coverageWarnings` | array | 生成覆盖告警（`NoBackendView` / `NoWebPage` / `OnlyBackendGenerated`） |
| `platformTableExcluded` | array | 平台表/排除表命中记录（字段同 `preview_generation`；`excluded=true` 表示未写盘，`excluded=false` 表示经 `includePlatformTables=true` 显式放行） |

**错误态**：同 `preview_generation`（`TemplatePackageError` / `InvalidArgument` / `InternalError`）。

**调用示例**：

```json
{
  "package": "my-package",
  "tables": [
    { "name": "t_user_info", "columns": [
      { "name": "id", "rawDbType": "bigint", "isPrimaryKey": true, "autoIncrement": true, "isNullable": false },
      { "name": "user_name", "rawDbType": "varchar", "isNullable": true, "length": 64 }
    ] }
  ],
  "workspaceRoot": "C:\\work\\demo-project",
  "codeDirectory": "src/main/java/com/example/demo",
  "files": ["entity.java.scriban", "mapper.java.scriban"],
  "duplicateStrategy": "skip",
  "variables": { "appName": "demo" },
  "modules": [ { "name": "account" } ],
  "webDirectory": "web",
  "includePlatformTables": false
}
```

### 4.16 `add_datasource`

**用途与使用时机**：新增数据源连接并落盘（流B第 2 步）。建议先用 `test_connection` 明文直测参数可用再保存。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `name` | string | 是 | — | 连接名称，唯一（忽略大小写） |
| `type` | string | 是 | — | 数据库类型：`mysql` / `postgresql`（亦接受 `postgres` 别名），大小写不敏感 |
| `host` | string | 是 | — | 主机名或 IP 地址 |
| `port` | integer | 是 | — | 端口号，1-65535 |
| `database` | string | 是 | — | 数据库名 |
| `userId` | string | 是 | — | 用户名 |
| `password` | string | 否 | null | 密码明文，仅瞬态用于 DPAPI 加密入库，不回显；缺省表示空密码 |

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `operation` | string | 固定 `add` |
| `datasource` | object | 落盘后的连接摘要（结构同 4.4 的 `datasources[]` 条目；不含密码字段） |

**错误态**：`InvalidArgument`（字段校验失败：空名、非法类型、端口越界等）；`NameConflict`（连接名已存在）；`SaveFailed`（落盘失败）。

**调用示例**：

```json
{ "name": "本地开发库", "type": "mysql", "host": "127.0.0.1", "port": 3306, "database": "demo", "userId": "root", "password": "secret" }
```

### 4.17 `update_datasource`

**用途与使用时机**：按原名称定位并更新已保存连接；**只传需要修改的字段**，未传字段保持原值。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `originalName` | string | 是 | — | 被更新连接的原始名称（区分大小写精确匹配） |
| `name` | string | 否 | null | 新连接名称；缺省保持原名 |
| `type` | string | 否 | null | 数据库类型（`mysql` / `postgresql`）；缺省保持原值 |
| `host` | string | 否 | null | 主机名或 IP；缺省保持原值 |
| `port` | integer | 否 | null | 端口（1-65535）；缺省保持原值 |
| `database` | string | 否 | null | 数据库名；缺省保持原值 |
| `userId` | string | 否 | null | 用户名；缺省保持原值 |
| `password` | string | 否 | null | 新密码明文；缺省或空白表示**保持已保存密码**（原密文不动） |

**返回**：同 4.16 结构，`operation` 固定 `update`。

**错误态**：`InvalidArgument`（字段校验失败）；`NameConflict`（新名称与其他连接冲突）；`OriginalMissing`（`originalName` 不存在，消息附可用连接名清单）；`SaveFailed`。

**调用示例**：

```json
{ "originalName": "本地开发库", "port": 3307 }
```

### 4.18 `delete_datasource`

**用途与使用时机**：按名称删除已保存的数据源连接并落盘。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `name` | string | 是 | — | 待删除连接的名称（区分大小写精确匹配） |

**返回**：同 4.16 结构，`operation` 固定 `delete`，`datasource` 为被删除连接的摘要。

**错误态**：`NotFound`（连接不存在）；`SaveFailed`（落盘失败）。

**调用示例**：

```json
{ "name": "本地开发库" }
```

### 4.19 `test_connection`

**用途与使用时机**：测试数据库连接可用性（10 秒超时兜底，失败信息经 Core 层脱敏）。两模式二选一：**按名模式**验证已保存连接（流B第 3 步）；**明文直测模式**不入库不留痕（流B第 1 步、流E排障）。

| 参数 | 类型 | 必填 | 默认 | 含义 |
| --- | --- | --- | --- | --- |
| `datasource` | string | 模式一必填 | null | 已保存的数据源连接名；传入即按名测试模式 |
| `type` | string | 模式二必填 | null | 直测数据库类型：`mysql` / `postgresql`（亦接受 `postgres`） |
| `host` | string | 模式二必填 | null | 直测主机名或 IP |
| `port` | integer | 模式二必填 | null | 直测端口，1-65535 |
| `database` | string | 模式二必填 | null | 直测数据库名 |
| `userId` | string | 模式二必填 | null | 直测用户名 |
| `password` | string | 否 | null | 直测密码明文，仅瞬态用于本次连接，不入库不回显；缺省按空密码尝试 |

**两模式互斥**：传了 `datasource` 再混入任何明文参数（`type/host/port/database/userId/password` 任一）会被直接拒绝，避免调用方误以为明文参数会生效。明文直测缺任一必填参数时，错误消息列出全部缺失项。

**返回**：

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `mode` | string | `saved` = 按已保存连接名测试；`direct` = 完整明文参数直测（不入库） |
| `datasource` | string \| 省略 | 按名测试的连接名；直测模式省略 |
| `success` | boolean | 连接是否成功 |
| `message` | string | 成功或失败的可读信息；失败原因经 Core 层脱敏，不含密码 |
| `serverVersion` | string \| 省略 | 服务端版本，连接成功时可能有值 |
| `elapsedMilliseconds` | integer | 连接耗时（毫秒） |

**错误态**：`InvalidArgument`（两模式混用；直测缺参；端口越界；数据源不存在）；`Timeout`（连接 10 秒兜底超时，固定文案提示检查主机/端口/网络）；`InternalError`。

**调用示例**：

```json
{ "datasource": "本地开发库" }
```

```json
{ "type": "postgresql", "host": "127.0.0.1", "port": 5432, "database": "demo", "userId": "postgres" }
```

## 5. 错误面契约

### 5.1 失败应答结构与识别口径

可预期的领域错误**不抛协议异常**，而是作为正常工具结果返回一段 JSON 文本：

```json
{ "isError": true, "errorType": "InvalidArgument", "message": "..." }
```

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `isError` | boolean | 恒为 `true`，程序化识别失败应答的标记 |
| `errorType` | string | 错误分类标识（见 5.2 全表） |
| `message` | string | 人类可读错误信息，来自 Core 服务的脱敏异常消息或参数校验消息，不含敏感字段 |

**识别口径（重要）**：实测确认，失败应答在 MCP 协议层的 `result.isError` 标志**不置位**（错误结果与成功结果都只携带 `content`）。AI 客户端必须**解析 content 文本中的 JSON**，以其中的 `isError: true` 判定失败——不要依赖协议层的 `result.isError`。

### 5.2 `errorType` 枚举全表

| errorType | 含义 | 典型触发场景 |
| --- | --- | --- |
| `InvalidArgument` | 入参非法 | 路径穿越/绝对路径/盘符前缀、空参数、非法枚举值（语言段/策略/数据库类型）、数据源不存在、`tables` 为空（所选模板含 table 作用域条目时）、端口越界、两模式混用 |
| `TemplatePackageError` | 模板包域/生成域错误 | 包不存在、模板路径未在包清单声明（`read_template` 的包根 `README.md.scriban` 窄例外除外）、内置包只读拒绝、模板文件同名冲突、模板渲染失败（含行列）、生成输出路径越出工作区根 |
| `Timeout` | 连接超时 | Core 连接 10 秒兜底超时触发（**内部超时，不等于调用方取消**）；固定文案「连接超时（10 秒兜底触发），请检查主机地址、端口与网络后重试。」 |
| `Cancelled` | 调用方取消 | 客户端主动取消本次请求 |
| `InternalError` | 其余内部错误 | 未归类异常，消息经 Core 层脱敏后透出 |
| `NameConflict` | 连接名冲突 | `add_datasource` 重名；`update_datasource` 改名撞既有连接 |
| `OriginalMissing` | 编辑目标不存在 | `update_datasource` 的 `originalName` 不存在（消息附可用连接名清单） |
| `NotFound` | 目标不存在 | `delete_datasource` 的 `name` 不存在 |
| `SaveFailed` | 配置落盘失败 | 连接增删改写 config.json 失败 |

`Timeout` 档语义辨析：连接类操作（`list_tables`/`get_table_detail`/`check_type_mapping`/`test_connection`）内部有 10 秒连接超时兜底；兜底触发链式取消时，服务端按「调用方未取消却收到取消异常」归类为 `Timeout` 并给出可诊断文案，与「调用方主动取消」（`Cancelled`）严格区分。

### 5.3 会话存活语义

**单次工具调用失败不终止 stdio 会话**：错误以结构化结果返回，连接保持可用，调用方修正参数后可直接重试。实测取证：连续多次失败调用（数据源不存在、路径穿越、非法语言段）后，后续请求均正常应答。

## 6. 约束红线

- **内置包只读机制**：内置模板包已移除，当前发布不含任何内置模板包（模板包全部来自用户模板库）。若部署侧自行放入内置包（`isBuiltin=true`），其编辑类操作（`save_template_content`/`create_template_file`/`delete_template_file`/`rename_template_file`）一律拒绝并返回 `TemplatePackageError`；需要定制时先复制到用户包。
- **路径安全（强制）**：
  - 模板路径（`read_template`/`render_template`/四个编辑工具的 `path`/`newPath`）：以包清单条目为白名单 + 禁止绝对路径、`..` 段与盘符前缀，必须落在包根目录内；清单未声明的路径一律拒绝。唯一窄例外（仅 `read_template`）：包根 `README.md.scriban` 即使未在清单声明也可读取（文档型条目：`enabled=false`、`output` 空串、不参与生成），读取仍经防目录穿越校验。
  - 生成输出路径：模板 `output` 渲染出的相对路径允许 `..` 段越出相对输出根（如从代码根 `src/main/java` 越到 `src/main/resources`），但解析后**必须仍位于工作区根内**，越出工作区根拒绝。
- **密码安全（强制）**：密码明文仅作为入参瞬态传递（加密入库或连接组装），任何应答字段、日志、错误消息都不回显明文与密文；`list_datasources` 及连接增删改的返回摘要是白名单投影，结构性不含密码字段；明文直测不入库不留痕。
- **参数全显式零记忆态**：服务端不读取桌面端的勾选态/排序/最近目录等 UI 记忆；`generate` 的表、模板选择、覆盖策略、输出位置全部显式传入；生成成功后不回写 config.json 的最近工作区根/代码目录（`persistLastUsedPaths` 固定关闭）。
- **与桌面端双开注意**：config 读写与模板包操作已由跨进程命名 Mutex 串行化，双开不会写坏文件；但桌面端**不监听模板文件变更**（无 FileSystemWatcher）——MCP 修改模板后，桌面端需重新加载包（重开「模板包管理」窗口或重启应用）才能看到最新内容。
- **写入前先预览**：`generate` 无确认弹窗，写盘动作以 `preview_generation` 的文件计划为准绳；`delete_template_file` 物理删除不可恢复，删除前先 `read_template` 或确认包内容。

## 7. 注册与日志速查

**注册（stdio）**：注册时提供 exe 全路径即可，无需参数。以发布产物为例（完整构建发布命令见 README「MCP 服务端」章节）：

```json
{
  "mcpServers": {
    "dbcodegen": {
      "command": "C:\\path\\to\\DbCodeGen.McpServer.exe",
      "args": []
    }
  }
}
```

**日志排障**：服务端日志固定落 `C:\simple\DbCodeGen.McpServer\logs\`（目录锚定，不随 exe 位置变化）：按天滚动、保留 7 份、单文件 20MB 上限、多实例共享写；逐请求工具调用审计（含失败与拒绝原因）在该目录 `dbcodegen-mcp-*.log`。日志初始化失败不阻塞服务，回落静默日志并在 exe 同目录留 `boot-error.log`。stdio 下 stdout 承载 MCP 协议帧，诊断信息一律看日志文件，不看控制台。
