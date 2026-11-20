using System;
using AIFramework.Contracts.Gm;
using AIFramework.Contracts.Ports;
using AIFramework.Core.Gm;
using Xunit;

namespace AIFramework.Core.Tests.Gm
{
    /// <summary>
    /// <see cref="GmConsole"/> 的行为契约：注册校验（名字字符集/重名/描述/参数个数）、
    /// 权限三档（Player &lt; Support &lt; Admin）、参数个数、处理器异常捕获、
    /// 审计写入与环形覆盖、审计升序副本、operatorId 归一、时间戳只取 <see cref="ITimeProvider"/>。
    /// </summary>
    public sealed class GmConsoleTests
    {
        private const string OkOutput = "ok";
        private const string HandlerFailureReason = "handler said no";

        // ------------------------------------------------------------------ 构造

        [Fact]
        public void Constructor_RejectsNullTimeProvider()
        {
            Assert.Throws<ArgumentNullException>(() => new GmConsole(null!));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void Constructor_RejectsNonPositiveAuditCapacity(int capacity)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GmConsole(new FakeTimeProvider(), capacity));
        }

        [Fact]
        public void NewConsole_HasNoCommandsAndNoAudit()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());

            Assert.Equal(0, gm.CommandCount);
            Assert.Empty(gm.Commands);
            Assert.Empty(gm.AuditLog);
        }

        // ------------------------------------------------------------------ 注册

        [Fact]
        public void Register_AddsCommandAndIncrementsCount()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());

            gm.Register(Command("gm.ping"), Returns(Ok()));

            Assert.Equal(1, gm.CommandCount);
            Assert.Equal("gm.ping", gm.Commands[0].Name);
        }

        [Fact]
        public void Register_KeepsAllDescriptorFields()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());

            gm.Register(
                new GmCommandDescriptor("gm.give", "发放道具", GmPermission.Admin, 2),
                Returns(Ok()));

            GmCommandDescriptor descriptor = gm.Commands[0];
            Assert.Equal("gm.give", descriptor.Name);
            Assert.Equal("发放道具", descriptor.Description);
            Assert.Equal(GmPermission.Admin, descriptor.MinimumPermission);
            Assert.Equal(2, descriptor.ArgumentCount);
        }

        [Fact]
        public void Register_PreservesRegistrationOrder()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());

            gm.Register(Command("gm.third"), Returns(Ok()));
            gm.Register(Command("gm.first"), Returns(Ok()));
            gm.Register(Command("gm.second"), Returns(Ok()));

            GmCommandDescriptor[] commands = gm.Commands;
            Assert.Equal("gm.third", commands[0].Name);
            Assert.Equal("gm.first", commands[1].Name);
            Assert.Equal("gm.second", commands[2].Name);
        }

        [Fact]
        public void Register_CommandsReturnsIndependentCopy()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.ping"), Returns(Ok()));

            GmCommandDescriptor[] copy = gm.Commands;
            copy[0] = Command("gm.hacked", GmPermission.Admin);

            Assert.Equal("gm.ping", gm.Commands[0].Name);
            Assert.Equal(1, gm.CommandCount);
        }

        [Fact]
        public void Register_DuplicateNameThrowsInvalidOperation()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.ping"), Returns(Ok()));

            Assert.Throws<InvalidOperationException>(() => gm.Register(Command("gm.ping"), Returns(Ok())));
            Assert.Equal(1, gm.CommandCount);
        }

        [Theory]
        [InlineData("gm.give")]
        [InlineData("gm.advance.time")]
        [InlineData("gm_give")]
        [InlineData("a1")]
        [InlineData("z9._")]
        public void Register_AcceptsAllowedNameCharacters(string name)
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());

            gm.Register(Command(name), Returns(Ok()));

            Assert.Equal(name, gm.Commands[0].Name);
        }

        [Theory]
        [InlineData("gm give")]
        [InlineData("gm-give")]
        [InlineData("gm/give")]
        [InlineData("gm@give")]
        [InlineData("gm.give!")]
        [InlineData("GM.give")]
        [InlineData("gm,give")]
        [InlineData("gm:give")]
        public void Register_RejectsIllegalNameCharacters(string name)
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());

            Assert.Throws<ArgumentException>(() => gm.Register(Command(name), Returns(Ok())));
            Assert.Equal(0, gm.CommandCount);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("\t")]
        public void Register_RejectsEmptyOrWhitespaceName(string? name)
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());

            Assert.Throws<ArgumentException>(() => gm.Register(Command(name!), Returns(Ok())));
            Assert.Equal(0, gm.CommandCount);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("\t")]
        public void Register_RejectsEmptyDescription(string description)
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            var descriptor = new GmCommandDescriptor("gm.ping", description, GmPermission.Player, 0);

            Assert.Throws<ArgumentException>(() => gm.Register(descriptor, Returns(Ok())));
            Assert.Equal(0, gm.CommandCount);
        }

        [Fact]
        public void Register_RejectsNegativeArgumentCount()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            var descriptor = new GmCommandDescriptor("gm.ping", "测试命令", GmPermission.Player, -1);

            Assert.Throws<ArgumentException>(() => gm.Register(descriptor, Returns(Ok())));
            Assert.Equal(0, gm.CommandCount);
        }

        [Fact]
        public void Register_RejectsNullHandler()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());

            Assert.Throws<ArgumentNullException>(() => gm.Register(Command("gm.ping"), null!));
            Assert.Equal(0, gm.CommandCount);
        }

        // ------------------------------------------------------------------ 执行：命令解析

        [Fact]
        public void Execute_UnknownCommandFails()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());

            GmCommandResult result = gm.Execute("gm.nope", Array.Empty<string>(), GmPermission.Admin, "op");

            Assert.False(result.Success);
            Assert.Equal("unknown command", result.Reason);
            Assert.Equal(string.Empty, result.Output);
        }

        [Fact]
        public void Execute_NullCommandNameFails()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());

            GmCommandResult result = gm.Execute(null!, Array.Empty<string>(), GmPermission.Admin, "op");

            Assert.False(result.Success);
            Assert.Equal("unknown command", result.Reason);
        }

        [Fact]
        public void Execute_CommandNameIsCaseSensitive()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.ping"), Returns(Ok()));

            GmCommandResult result = gm.Execute("GM.PING", Array.Empty<string>(), GmPermission.Admin, "op");

            Assert.False(result.Success);
            Assert.Equal("unknown command", result.Reason);
        }

        [Fact]
        public void Execute_ZeroArgumentCommandAcceptsNullArgumentArray()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.ping"), Returns(Ok("pong")));

            GmCommandResult result = gm.Execute("gm.ping", null!, GmPermission.Player, "op");

            Assert.True(result.Success);
            Assert.Equal("pong", result.Output);
        }

        [Fact]
        public void Execute_NullArgumentsOnParameterizedCommandFailsBadArguments()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.give", GmPermission.Admin, 2), Returns(Ok()));

            GmCommandResult result = gm.Execute("gm.give", null!, GmPermission.Admin, "op");

            Assert.False(result.Success);
            Assert.Equal("bad arguments", result.Reason);
        }

        // ------------------------------------------------------------------ 执行：权限分级

        [Fact]
        public void Execute_PlayerCannotRunAdminCommand()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.give", GmPermission.Admin, 2), Returns(Ok()));

            GmCommandResult result = gm.Execute("gm.give", new[] { "1001", "1" }, GmPermission.Player, "op");

            Assert.False(result.Success);
            Assert.Equal("permission denied", result.Reason);
        }

        [Fact]
        public void Execute_SupportCannotRunAdminCommand()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.give", GmPermission.Admin, 2), Returns(Ok()));

            GmCommandResult result = gm.Execute("gm.give", new[] { "1001", "1" }, GmPermission.Support, "op");

            Assert.False(result.Success);
            Assert.Equal("permission denied", result.Reason);
        }

        [Fact]
        public void Execute_PermissionDeniedDoesNotInvokeHandler()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            bool invoked = false;
            gm.Register(Command("gm.give", GmPermission.Admin, 0), args =>
            {
                invoked = true;
                return Ok();
            });

            gm.Execute("gm.give", Array.Empty<string>(), GmPermission.Player, "op");

            Assert.False(invoked);
        }

        [Theory]
        [InlineData(GmPermission.Support)]
        [InlineData(GmPermission.Admin)]
        public void Execute_HigherPermissionCanRunSupportCommand(GmPermission permission)
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.reset.guide", GmPermission.Support), Returns(Ok()));

            GmCommandResult result = gm.Execute("gm.reset.guide", Array.Empty<string>(), permission, "op");

            Assert.True(result.Success);
        }

        [Theory]
        [InlineData(GmPermission.Player)]
        [InlineData(GmPermission.Support)]
        [InlineData(GmPermission.Admin)]
        public void Execute_AnyPermissionCanRunPlayerCommand(GmPermission permission)
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.stats"), Returns(Ok()));

            GmCommandResult result = gm.Execute("gm.stats", Array.Empty<string>(), permission, "op");

            Assert.True(result.Success);
        }

        [Fact]
        public void Execute_PermissionCheckedBeforeArgumentCount()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.give", GmPermission.Admin, 2), Returns(Ok()));

            // 越权 + 参数个数也不对：原因必须是 permission denied（不能从原因码反推命令签名）。
            GmCommandResult result = gm.Execute("gm.give", Array.Empty<string>(), GmPermission.Player, "op");

            Assert.False(result.Success);
            Assert.Equal("permission denied", result.Reason);
        }

        // ------------------------------------------------------------------ 执行：参数与结果

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        public void Execute_BadArgumentCountFails(int supplied)
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.give", GmPermission.Admin, 2), Returns(Ok()));

            var arguments = new string[supplied];
            for (int i = 0; i < supplied; i++)
            {
                arguments[i] = i.ToString();
            }

            GmCommandResult result = gm.Execute("gm.give", arguments, GmPermission.Admin, "op");

            Assert.False(result.Success);
            Assert.Equal("bad arguments", result.Reason);
        }

        [Fact]
        public void Execute_HandlerReceivesArgumentsVerbatim()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            string[]? received = null;
            gm.Register(Command("gm.give", GmPermission.Admin, 3), args =>
            {
                received = args;
                return Ok();
            });

            gm.Execute("gm.give", new[] { "1001", "100", "note" }, GmPermission.Admin, "op");

            Assert.NotNull(received);
            Assert.Equal(3, received!.Length);
            Assert.Equal("1001", received[0]);
            Assert.Equal("100", received[1]);
            Assert.Equal("note", received[2]);
        }

        [Fact]
        public void Execute_ReturnsHandlerSuccessOutput()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.ping"), Returns(Ok("pong")));

            GmCommandResult result = gm.Execute("gm.ping", Array.Empty<string>(), GmPermission.Player, "op");

            Assert.True(result.Success);
            Assert.Equal("pong", result.Output);
            Assert.Equal(string.Empty, result.Reason);
        }

        [Fact]
        public void Execute_ReturnsHandlerFailureReason()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.ping"), Returns(Failed()));

            GmCommandResult result = gm.Execute("gm.ping", Array.Empty<string>(), GmPermission.Player, "op");

            Assert.False(result.Success);
            Assert.Equal(HandlerFailureReason, result.Reason);
        }

        [Fact]
        public void Execute_HandlerExceptionBecomesFailure()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.boom"), args => throw new InvalidOperationException("boom"));

            GmCommandResult result = gm.Execute("gm.boom", Array.Empty<string>(), GmPermission.Player, "op");

            Assert.False(result.Success);
            Assert.Equal("handler threw: InvalidOperationException", result.Reason);
            Assert.Equal(string.Empty, result.Output);
        }

        [Fact]
        public void Execute_HandlerCustomExceptionNamesConcreteType()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.boom"), args => throw new BoomException());

            GmCommandResult result = gm.Execute("gm.boom", Array.Empty<string>(), GmPermission.Player, "op");

            Assert.False(result.Success);
            Assert.Equal("handler threw: BoomException", result.Reason);
        }

        [Fact]
        public void Execute_EveryCallWritesExactlyOneAuditEntry()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.ping"), Returns(Ok()));

            gm.Execute("gm.ping", Array.Empty<string>(), GmPermission.Player, "op"); // 成功
            gm.Execute("gm.nope", Array.Empty<string>(), GmPermission.Player, "op"); // 未知命令
            gm.Execute("gm.ping", new[] { "x" }, GmPermission.Player, "op");         // 参数不符

            Assert.Equal(3, gm.AuditLog.Length);
        }

        // ------------------------------------------------------------------ 审计

        [Fact]
        public void Audit_SuccessEntryCarriesAllFields()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.give", GmPermission.Admin, 2), Returns(Ok("item 1001 -> 100")));

            gm.Execute("gm.give", new[] { "1001", "100" }, GmPermission.Admin, "op-1");

            GmAuditEntry entry = gm.AuditLog[0];
            Assert.Equal("gm.give", entry.Command);
            Assert.Equal("1001 100", entry.Arguments);
            Assert.Equal("op-1", entry.OperatorId);
            Assert.Equal(GmPermission.Admin, entry.Permission);
            Assert.True(entry.Success);
            Assert.Equal("item 1001 -> 100", entry.Output);
        }

        [Fact]
        public void Audit_FailureEntryIsWritten()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.ping"), Returns(Failed()));

            gm.Execute("gm.ping", Array.Empty<string>(), GmPermission.Player, "op");

            GmAuditEntry entry = gm.AuditLog[0];
            Assert.False(entry.Success);
            Assert.Equal(string.Empty, entry.Output);
            Assert.Equal("gm.ping", entry.Command);
        }

        [Fact]
        public void Audit_PermissionDeniedIsWrittenWithCallerPermission()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.give", GmPermission.Admin, 2), Returns(Ok()));

            gm.Execute("gm.give", new[] { "1001", "1" }, GmPermission.Player, "intruder");

            GmAuditEntry entry = gm.AuditLog[0];
            Assert.False(entry.Success);
            Assert.Equal("gm.give", entry.Command);
            Assert.Equal(GmPermission.Player, entry.Permission);
            Assert.Equal("intruder", entry.OperatorId);
        }

        [Fact]
        public void Audit_UnknownCommandIsWritten()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());

            gm.Execute("gm.nope", Array.Empty<string>(), GmPermission.Support, "op");

            GmAuditEntry entry = gm.AuditLog[0];
            Assert.False(entry.Success);
            Assert.Equal("gm.nope", entry.Command);
            Assert.Equal(GmPermission.Support, entry.Permission);
        }

        [Fact]
        public void Audit_BadArgumentsIsWritten()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.give", GmPermission.Admin, 2), Returns(Ok()));

            gm.Execute("gm.give", new[] { "1001" }, GmPermission.Admin, "op");

            GmAuditEntry entry = gm.AuditLog[0];
            Assert.False(entry.Success);
            Assert.Equal("1001", entry.Arguments);
        }

        [Fact]
        public void Audit_RingOverwritesOldestEntry()
        {
            var time = new FakeTimeProvider();
            GmConsole gm = CreateConsole(time, auditCapacity: 3);
            gm.Register(Command("gm.a"), Returns(Ok()));
            gm.Register(Command("gm.b"), Returns(Ok()));
            gm.Register(Command("gm.c"), Returns(Ok()));
            gm.Register(Command("gm.d"), Returns(Ok()));
            gm.Register(Command("gm.e"), Returns(Ok()));

            gm.Execute("gm.a", Array.Empty<string>(), GmPermission.Player, "op");
            gm.Execute("gm.b", Array.Empty<string>(), GmPermission.Player, "op");
            gm.Execute("gm.c", Array.Empty<string>(), GmPermission.Player, "op");
            gm.Execute("gm.d", Array.Empty<string>(), GmPermission.Player, "op");
            gm.Execute("gm.e", Array.Empty<string>(), GmPermission.Player, "op");

            GmAuditEntry[] audit = gm.AuditLog;
            Assert.Equal(3, audit.Length);
            Assert.Equal("gm.c", audit[0].Command);
            Assert.Equal("gm.d", audit[1].Command);
            Assert.Equal("gm.e", audit[2].Command);
        }

        [Fact]
        public void Audit_ReturnsAscendingByExecutionTime()
        {
            var time = new FakeTimeProvider();
            GmConsole gm = CreateConsole(time);
            gm.Register(Command("gm.ping"), Returns(Ok()));

            gm.Execute("gm.ping", Array.Empty<string>(), GmPermission.Player, "op");
            time.SetUtcNow(time.UtcNow.AddSeconds(5));
            gm.Execute("gm.ping", Array.Empty<string>(), GmPermission.Player, "op");
            time.SetUtcNow(time.UtcNow.AddSeconds(5));
            gm.Execute("gm.ping", Array.Empty<string>(), GmPermission.Player, "op");

            GmAuditEntry[] audit = gm.AuditLog;
            Assert.True(audit[0].ExecutedAtUnixSeconds < audit[1].ExecutedAtUnixSeconds);
            Assert.True(audit[1].ExecutedAtUnixSeconds < audit[2].ExecutedAtUnixSeconds);
        }

        [Fact]
        public void Audit_ReturnsIndependentCopy()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.ping"), Returns(Ok()));
            gm.Execute("gm.ping", Array.Empty<string>(), GmPermission.Player, "op");

            GmAuditEntry[] copy = gm.AuditLog;
            copy[0] = default;

            Assert.Equal("gm.ping", gm.AuditLog[0].Command);
        }

        [Fact]
        public void Audit_UsesTimeProviderUtcNow()
        {
            var time = new FakeTimeProvider();
            var utc = new DateTime(2026, 11, 20, 8, 30, 0, DateTimeKind.Utc);
            time.SetUtcNow(utc);

            GmConsole gm = CreateConsole(time);
            gm.Register(Command("gm.ping"), Returns(Ok()));
            gm.Execute("gm.ping", Array.Empty<string>(), GmPermission.Player, "op");

            Assert.Equal(new DateTimeOffset(utc).ToUnixTimeSeconds(), gm.AuditLog[0].ExecutedAtUnixSeconds);
        }

        [Fact]
        public void Audit_TreatsUnspecifiedKindAsUtc()
        {
            var time = new FakeTimeProvider();
            time.SetUtcNow(new DateTime(2026, 11, 20, 8, 30, 0, DateTimeKind.Unspecified));

            GmConsole gm = CreateConsole(time);
            gm.Register(Command("gm.ping"), Returns(Ok()));
            gm.Execute("gm.ping", Array.Empty<string>(), GmPermission.Player, "op");

            // 宿主时区不参与计算：同一 UTC 字面量在任何机器上都得到同一个时间戳。
            long expected = new DateTimeOffset(new DateTime(2026, 11, 20, 8, 30, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
            Assert.Equal(expected, gm.AuditLog[0].ExecutedAtUnixSeconds);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Audit_EmptyOperatorIdBecomesUnknown(string? operatorId)
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.ping"), Returns(Ok()));

            gm.Execute("gm.ping", Array.Empty<string>(), GmPermission.Player, operatorId!);

            Assert.Equal("unknown", gm.AuditLog[0].OperatorId);
        }

        [Fact]
        public void Audit_PreservesNonEmptyOperatorId()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.ping"), Returns(Ok()));

            gm.Execute("gm.ping", Array.Empty<string>(), GmPermission.Player, "gm-operator-42");

            Assert.Equal("gm-operator-42", gm.AuditLog[0].OperatorId);
        }

        [Fact]
        public void Audit_ArgumentsJoinedWithSingleSpaces()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.give", GmPermission.Admin, 3), Returns(Ok()));

            gm.Execute("gm.give", new[] { "1001", "100", "note" }, GmPermission.Admin, "op");

            Assert.Equal("1001 100 note", gm.AuditLog[0].Arguments);
        }

        [Fact]
        public void Audit_NoArgumentCommandRecordsEmptyArguments()
        {
            GmConsole gm = CreateConsole(new FakeTimeProvider());
            gm.Register(Command("gm.ping"), Returns(Ok()));

            gm.Execute("gm.ping", null!, GmPermission.Player, "op");

            Assert.Equal(string.Empty, gm.AuditLog[0].Arguments);
        }

        // ------------------------------------------------------------------ 测试替身与辅助

        private static GmConsole CreateConsole(FakeTimeProvider time, int auditCapacity = 16)
        {
            return new GmConsole(time, auditCapacity);
        }

        private static GmCommandDescriptor Command(
            string name,
            GmPermission permission = GmPermission.Player,
            int argumentCount = 0,
            string description = "测试命令")
        {
            return new GmCommandDescriptor(name, description, permission, argumentCount);
        }

        private static Func<string[], GmCommandResult> Returns(GmCommandResult result)
        {
            return args => result;
        }

        private static GmCommandResult Ok(string output = OkOutput)
        {
            return new GmCommandResult(true, output, string.Empty);
        }

        private static GmCommandResult Failed(string reason = HandlerFailureReason)
        {
            return new GmCommandResult(false, string.Empty, reason);
        }

        /// <summary>可控时间源：只实现审计需要的那两个成员，测试里显式设定 UTC 时刻。</summary>
        private sealed class FakeTimeProvider : ITimeProvider
        {
            private DateTime _utcNow = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

            public double ElapsedSeconds { get; private set; }

            public DateTime UtcNow
            {
                get { return _utcNow; }
            }

            public void SetUtcNow(DateTime utcNow)
            {
                _utcNow = utcNow;
            }
        }

        /// <summary>用来验证"异常类型名进 reason"的自定义异常。</summary>
        private sealed class BoomException : Exception
        {
            public BoomException()
                : base("boom")
            {
            }
        }
    }
}
