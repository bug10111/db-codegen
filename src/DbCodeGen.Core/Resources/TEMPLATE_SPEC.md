# TEMPLATE_SPEC 模板规范（AI 模板生成用）

> 本文档是 AI 模板生成功能的元数据模型规范，供 LLM 在生成模板包时对照编写。
> 元数据模型字段契约来自 02-表浏览与选择.md §七；渲染上下文与 tool 函数来自 04-模板编辑与预览.md §六/§七；manifest 结构来自 03-模板包管理.md §6.2。本文档是单点维护来源，LLM 输出必须严格按本文档生成，保证生成的模板包可被 TemplatePackageLoader 校验加载。

## 一、模板渲染上下文变量

模板（Scriban）渲染时可用的顶层变量为 `table`（表元数据）、`column`（当前列，遍历列时可用）、`package`（包上下文）、`tool`（函数集）。字段名严格以下表为准，不得使用 `name` 等别名。

### 1.1 table 表元数据（TableInfo）

| 变量表达式 | 字段 | 类型 | 说明 |
|------|------|------|------|
| `{{ table.rawName }}` | RawName | string | 原始表名，与数据库中实际表名一致 |
| `{{ table.schemaName }}` | SchemaName | string? | 所属 schema/库名，可空 |
| `{{ table.className }}` | ClassName | string | 类名，PascalCase，按表名实时转换 |
| `{{ table.variableName }}` | VariableName | string | 变量名，camelCase |
| `{{ table.comment }}` | Comment | string? | 表注释，无注释时为空 |
| `{{ table.columns }}` | Columns | List\<ColumnInfo\> | 全部列集合 |
| `{{ table.primaryKeys }}` | PrimaryKeys | List\<ColumnInfo\> | 主键列集合 |
| `{{ table.fullColumn }}` | FullColumn | List\<ColumnInfo\> | 全量列集合（与 Columns 同源） |
| `{{ table.otherColumn }}` | OtherColumn | List\<ColumnInfo\> | 非主键列集合 |

### 1.2 column 列元数据（ColumnInfo）

| 变量表达式 | 字段 | 类型 | 说明 |
|------|------|------|------|
| `{{ column.rawName }}` | RawName | string | 原始列名 |
| `{{ column.propertyName }}` | PropertyName | string | 驼峰属性名 |
| `{{ column.comment }}` | Comment | string? | 列注释 |
| `{{ column.rawDbType }}` | RawDbType | string | 原始 DB 类型，如 varchar、bigint、timestamp（不含长度修饰） |
| `{{ column.isPrimaryKey }}` | IsPrimaryKey | bool | 是否主键列 |
| `{{ column.autoIncrement }}` | AutoIncrement | bool | 是否自增列 |
| `{{ column.isNullable }}` | IsNullable | bool | 是否可空 |
| `{{ column.defaultValue }}` | DefaultValue | string? | 默认值，可空 |
| `{{ column.length }}` | Length | int? | 列长度（varchar 等），可空 |
| `{{ column.precision }}` | Precision | int? | 精度（numeric/decimal），可空 |
| `{{ column.scale }}` | Scale | int? | 小数位（numeric/decimal），可空 |

### 1.3 package 包上下文（TemplatePackageContext）

| 变量表达式 | 字段 | 说明 |
|------|------|------|
| `{{ package.name }}` | Name | 包名 |
| `{{ package.basePackage }}` | BasePackage | 基础包名，可填完整包名（含模块段，如 com.example.common），可为空 |
| `{{ package.dir }}` | Dir | 输出目录占位，完整包名按点号全量转斜杠（如 com.example.common → com/example/common） |

> 注意：`table.name`、`column.name` 不存在，一律用 `table.rawName` / `table.className` / `column.rawName` / `column.propertyName`。

## 二、tool 函数集

模板内通过 `tool.函数名(参数)` 调用，入参为字符串或列元数据字段。

| 函数 | 表达式示例 | 语义 |
|------|------|------|
| 首字母小写 | `{{ tool.firstLowerCase(table.className) }}` | 如 SysUser → sysUser |
| 首字母大写 | `{{ tool.firstUpperCase(table.variableName) }}` | 如 sysUser → SysUser |
| 驼峰转下划线小写 | `{{ tool.hump2Underline(table.className) }}` | 如 SysUser → sys_user，SysURLConfig → sys_url_config |
| 驼峰转短横线小写 | `{{ tool.hump3Underline(table.className) }}` | 如 SysUser → sys-user |
| 类型映射 | `{{ tool.type(column.rawDbType) }}` | 解析链：当前数据库类型条目 → 通用条目 → 包 typeMap → 默认 String |
| 类型导包 | `{{ tool.typeImport(column.rawDbType) }}` | 返回该类型映射声明的导包（如 java.util.Date），无导包需求返回空串 |
| 列集合导包块 | `{{ tool.imports(table.fullColumn) }}` | 对列集合去重生成 `import X;` 语句块，供实体模板自动导包，无导包返回空串 |

> `tool.type` 映射解析链为：**当前数据库类型专属条目（设置 → 类型映射）→ 通用条目 → 当前包 manifest `typeMap` → 默认 String**。
> 映射与表所属数据库类型挂钩（MySQL / PostgreSQL），`tool.typeImport` / `tool.imports` 的导包信息仅全局映射条目可提供（`typeMap` 为纯字符串映射不含导包）。

## 三、template.json manifest 规范

生成包落盘前必须映射为如下 template.json 结构（与 03 模板包管理 §6.2 一致），engine 固定为 `scriban`。

```json
{
  "name": "包名",
  "description": "包说明",
  "engine": "scriban",
  "basePackage": "com.example.common",
  "typeMap": {
    "bigint": "Long",
    "varchar": "String"
  },
  "files": [
    {
      "template": "entity.java.scriban",
      "output": "{{package.dir}}/entity/{{table.className}}.java",
      "enabled": true
    },
    {
      "template": "mapper.xml.scriban",
      "output": "../resources/mapper/{{table.className}}Dao.xml",
      "enabled": true
    }
  ]
}
```

### 字段约束

- `name`：目录名规则，仅字母/数字/中划线/下划线，至少含一个字母或数字；禁止路径分隔符、`..`、绝对路径。
- `engine`：固定 `scriban`，不允许其它引擎。
- `basePackage`：可空；可填完整包名（含模块段，如 com.example.common），非空时作为生成上下文注入 `package.basePackage`，`package.dir` 按点号全量转目录。
- `typeMap`：可选；Dictionary\<string,string\>，键为数据库原始类型（大小写不敏感命中），值为目标语言类型。作为全局映射表之下的兜底，未声明时生成走全局映射表。
- `files[].template`：模板文件相对包根路径，禁止绝对路径与 `..` 段（防目录穿越）；模板文件必须与落盘的 Scriban 文件一一对应。
- `files[].output`：输出相对路径，支持 `{{变量}}` 占位（如 `{{package.dir}}/entity/{{table.className}}.java`）；禁止绝对路径与盘符前缀；允许 `..` 段越出代码根（如 `../resources/mapper/{{table.className}}Dao.xml`）落到 `src/main/resources` 等代码根外目录，但解析后必须落在工作区根内。
- `files[].enabled`：是否默认勾选参与生成，缺省 true。

## 四、Scriban 用法示例

Scriban 语句标签为 `{{ }}`，`{% %}` 按字面输出，不要使用 Liquid 风格标签。

> **输出排版规范（必须遵守）**：
> - **注解/注释与其字段必须紧邻**：注解行与字段行之间只允许一个换行，中间不得放置控制标签或空行（如 `@Schema(...)` 的下一行紧跟 `private ...;`）。渲染引擎会自动删除注解行（以 `)` 结尾）后的空行作为兜底，但模板仍应避免在注解与字段之间放置控制标签。
> - **成员之间保留一个空行**：字段与字段之间用一个空行分隔。
> - 控制标签（`{{ for }}`/`{{ if }}`/`{{ end }}`）独占一行即可，渲染引擎会自动把连续 2 个及以上空行规整为单个空行（安全兜底），不必为消除空行刻意使用 `{{~ ~}}`/`{{- -}}`。

### 4.1 遍历列

```scriban
{{ for column in table.fullColumn }}
    @Schema(description = "{{ column.comment }}")
    private {{ tool.type(column.rawDbType) }} {{ column.propertyName }};

{{ end }}
```

### 4.2 主键遍历与条件

```scriban
{{ for pk in table.primaryKeys }}
    @Id @GeneratedValue(strategy = GenerationType.IDENTITY)
    private {{ tool.type(pk.rawDbType) }} {{ pk.propertyName }};

{{ end }}
```

### 4.3 类名与包名

```scriban
package {{ package.basePackage }}.{{ table.variableName }};

/**
 * {{ table.comment }}
 */
public class {{ table.className }} {
```

### 4.4 输出路径占位

输出相对路径中使用 `{{package.dir}}`、`{{table.className}}`、`{{table.variableName}}` 占位，如：

```
{{package.dir}}/entity/{{table.className}}.java
{{package.dir}}/mapper/{{table.className}}Mapper.java
../resources/mapper/{{table.className}}Dao.xml
```

输出相对路径以代码根（如 `src/main/java`）为基准；要落到 `src/main/resources` 等代码根外目录时用 `..` 越级（如 `../resources/mapper/...`），解析后必须仍位于工作区根内，禁止携带 `src/` 等绝对前缀。

路径占位除 `{{package.dir}}`、`{{table.className}}`、`{{table.variableName}}` 外，同样可使用 `tool.*` 函数（如 `{{tool.firstLowerCase(table.className)}}`、`{{tool.hump3Underline(table.className)}}`），与内容渲染一致。

## 五、生成的模板包校验要点

- 每个 `files[].template` 声明的文件必须真实写入包目录，缺失文件会在校验阶段被拒绝。
- `output` 必须满足相对路径静态骨架校验（非空、非绝对路径、无盘符前缀）；允许 `..` 段越出代码根，但解析后必须落在工作区根内。
- 至少声明一个模板文件，`files` 不允许为空。
- 模板文件内容使用 Scriban 语法，字段名严格对齐第一节变量表，保证渲染阶段可解析。

## 六、参考文件作为约定蓝本

生成请求携带参考文件时，参考文件是用户既有模板/代码的约定蓝本，应作为蓝本逐文件镜像：每个参考文件尽量生成一个对应模板，文件名与相对结构对齐，并把 Velocity/FreeMarker/EasyCode 模板语法翻译为 Scriban，保持输出代码风格、注解、import 与包结构一致。除非生成说明明确要求只生成部分模板，否则数量与参考文件对齐；无参考文件时按生成说明自行决定范围。

### 6.1 逐文件镜像要求

- 每个参考文件尽量生成一个对应 Scriban 模板，数量、文件名与相对结构与参考文件对齐。
- 参考文件为 Velocity/FreeMarker/EasyCode 模板配置时，把其模板语法翻译为 Scriban，产出同风格代码。
- 普通代码参考文件提炼其命名、结构、注解、import 与包结构约定，覆盖相同产出范围。

### 6.2 Velocity/EasyCode → Scriban 变量映射

EasyCode 模板以 `tableInfo`（表）、`columnInfo`（列）为上下文，翻译时映射到本工具渲染变量：

| EasyCode/Velocity 表达式 | Scriban 表达式 | 说明 |
|------|------|------|
| `$!{tableInfo.name}` | `{{ table.rawName }}` | 原始表名 |
| `$!{tableInfo.className}` | `{{ table.className }}` | 类名，PascalCase |
| `$!{tableInfo.variableName}` | `{{ table.variableName }}` | 变量名，camelCase |
| `$!{tableInfo.comment}` | `{{ table.comment }}` | 表注释 |
| `$!{columnInfo.name}` | `{{ column.propertyName }}` | 列属性名，驼峰 |
| `$!{columnInfo.comment}` | `{{ column.comment }}` | 列注释 |
| `$!{columnInfo.rawName}` | `{{ column.rawName }}` | 列原始名 |
| `$!{columnInfo.type}` | `{{ tool.type(column.rawDbType) }}` | 类型映射 |
| `#foreach(...)` | `{{~ for column in table.fullColumn ~}}` / `{{~ end ~}}` | 列遍历 |
| `#if(...)` / `#else` | `{{~ if ... ~}}` / `{{~ else ~}}` / `{{~ end ~}}` | 条件分支 |
| 主键相关判断 | `{{ table.primaryKeys }}` 遍历 | 主键集合 |

### 6.3 输出与命名约定映射

- `#save(path, ext)` 宏对应 `files[].name`（模板相对路径）+ `files[].relativeOutputPath`（输出相对路径），保留参考文件声明的输出结构。
- `#setPackageSuffix2`、`#setPackageSuffix` 等包结构宏按参考文件的包结构约定映射为输出路径中的目录段（如 `{{package.dir}}/xxx`）。
- 分组目录前缀仅影响模板文件组织，不影响生成代码落盘路径；`files[].name` 不包含分组前缀。
