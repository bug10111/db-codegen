using System.Runtime.CompilerServices;

// 测试工程可见性：MCP 工具面内部组件（如生成请求组装工厂）的单元测试经 DbCodeGen.Core.Tests 直达，
// 与 Core 工程的 InternalsVisibleTo 口径一致。
[assembly: InternalsVisibleTo("DbCodeGen.Core.Tests")]
