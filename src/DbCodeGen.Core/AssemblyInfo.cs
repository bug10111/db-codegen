using System.Runtime.CompilerServices;

// 向单元测试工程开放内部成员访问，便于测试连接服务的密码解析与脱敏等内部契约
[assembly: InternalsVisibleTo("DbCodeGen.Core.Tests")]
