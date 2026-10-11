# java-mybatis-plus 内置模板包

Java SpringBoot + MyBatis-Plus 持久层六件套模板包：按表生成 Entity / Mapper 接口 / Mapper XML / Service 接口 / ServiceImpl / Controller 全套代码，类型映射清单内置常见数据库类型到 Java 类型的对应关系，开箱即用。

本包为内置包（随应用分发至 `Templates\Builtin\java-mybatis-plus\`），只读不可变；需要定制时用 `copy_template_package` 复制为用户包后修改。

## 模板清单

| 模板 | 输出路径 | 默认启用 |
|---|---|---|
| entity.java.scriban | `{{package.dir}}/entity/{{table.className}}.java` | 是 |
| mapper.java.scriban | `{{package.dir}}/mapper/{{table.className}}Mapper.java` | 是 |
| mapper.xml.scriban | `{{package.dir}}/mapper/xml/{{table.className}}Mapper.xml` | 是 |
| service.java.scriban | `{{package.dir}}/service/{{table.className}}Service.java` | 是 |
| serviceImpl.java.scriban | `{{package.dir}}/service/impl/{{table.className}}ServiceImpl.java` | 是 |
| controller.java.scriban | `{{package.dir}}/controller/{{table.className}}Controller.java` | 否 |

## 输出路径说明

- `{{package.dir}}` 由调用方 `codeDirectory` 派生：传完整相对路径（如 `src/main/java/com/example/common`）时按该包路径落盘，纯包名形态默认输出根 `src/main/java`。
- 每张表生成一组文件：实体落 `entity/`、Mapper 接口与 XML 落 `mapper/`（XML 在 `mapper/xml/`）、Service 接口落 `service/`、实现落 `service/impl/`、Controller 落 `controller/`，文件名按表名派生的 PascalCase 类名。
- 基础包名取包清单 `basePackage`（`com.example.common`），可在生成调用中经 `basePackageOverride` 覆盖。

## 调用配方

1. `list_templates`（package=`java-mybatis-plus`）查看模板条目与输出路径配方。
2. `preview_generation` 传合成或实查表集合（`tables[].name` 必填，列元数据可选）+ `workspaceRoot` + `codeDirectory`，零写盘确认文件计划与未映射类型清单。
3. 确认计划后 `generate` 写盘；同名文件按 `duplicateStrategy` 处理（含手写代码的目录用 `skip`）。
4. 单模板试渲染用 `render_template`：传 `package` + `path` + 合成表上下文，返回渲染全文不落盘。

未在包类型映射（`typeMap`）中的数据库类型会出现在未映射类型清单里，先经 `check_type_mapping` 预检或用 `update_template_package_metadata` 补充映射再生成。

## 启用 Controller 模板

`controller.java.scriban` 默认禁用（清单条目 `enabled=false`）：生成工具省略 `files` 清单时只生成启用条目。需要按表生成 Controller 时，在桌面端模板管理界面将该模板切换为启用（写回包清单），或将该模板路径加入生成调用的 `files` 过滤清单。
