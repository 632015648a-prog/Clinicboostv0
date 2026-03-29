using System.Data;
using System.Data.Common;
using ClinicBoost.Api.Infrastructure.Database;
using ClinicBoost.Api.Infrastructure.Tenants;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ClinicBoost.Tests.Infrastructure.Database;

/// <summary>
/// Tests unitarios del TenantDbContextInterceptor.
/// Verifica que claim_tenant_context() se llama (o no) según el estado
/// del ITenantContext, y que los parámetros SQL son los correctos.
/// </summary>
public sealed class TenantDbContextInterceptorTests
{
    // ── Helpers de fakes ──────────────────────────────────────────────────────

    /// <summary>
    /// Fake de DbConnection que captura los comandos ejecutados.
    /// </summary>
    private sealed class FakeConnection : DbConnection
    {
        public List<(string sql, Dictionary<string, object?> @params)> ExecutedCommands { get; } = [];

        protected override DbTransaction BeginDbTransaction(IsolationLevel il) =>
            throw new NotSupportedException();
        public override void ChangeDatabase(string name)  => throw new NotSupportedException();
        public override void Close()  { }
        public override void Open()   { }
        public override Task OpenAsync(CancellationToken ct) => Task.CompletedTask;
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database         => "test";
        public override string DataSource       => "test";
        public override string ServerVersion    => "test";
        public override ConnectionState State   => ConnectionState.Open;

        protected override DbCommand CreateDbCommand() => new FakeCommand(this);
    }

    private sealed class FakeCommand : DbCommand
    {
        private readonly FakeConnection _conn;
        private readonly FakeParameterCollection _params = new();

        public FakeCommand(FakeConnection conn) => _conn = conn;

        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override int ExecuteNonQuery()
        {
            _conn.ExecutedCommands.Add((CommandText, _params.ToDictionary()));
            return 0;
        }
        public override Task<int> ExecuteNonQueryAsync(CancellationToken ct)
        {
            _conn.ExecutedCommands.Add((CommandText, _params.ToDictionary()));
            return Task.FromResult(0);
        }
        public override object? ExecuteScalar() => null;
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior b) =>
            throw new NotSupportedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeParameter();
    }

    private sealed class FakeParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeParameterCollection : DbParameterCollection
    {
        private readonly List<FakeParameter> _items = [];

        public override int Count => _items.Count;
        public override object SyncRoot => _items;

        public override int Add(object value)        { _items.Add((FakeParameter)value); return _items.Count - 1; }
        public override void AddRange(Array values)  => throw new NotSupportedException();
        public override void Clear()                 => _items.Clear();
        public override bool Contains(object value)  => _items.Contains(value);
        public override bool Contains(string name)   => _items.Any(p => p.ParameterName == name);
        public override void CopyTo(Array arr, int i)=> throw new NotSupportedException();
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value)    => _items.IndexOf((FakeParameter)value);
        public override int IndexOf(string name)     => _items.FindIndex(p => p.ParameterName == name);
        public override void Insert(int i, object v) => _items.Insert(i, (FakeParameter)v);
        public override void Remove(object value)    => _items.Remove((FakeParameter)value);
        public override void RemoveAt(int index)     => _items.RemoveAt(index);
        public override void RemoveAt(string name)   => _items.RemoveAll(p => p.ParameterName == name);
        protected override DbParameter GetParameter(int i)    => _items[i];
        protected override DbParameter GetParameter(string n) => _items.First(p => p.ParameterName == n);
        protected override void SetParameter(int i, DbParameter v)    => _items[i] = (FakeParameter)v;
        protected override void SetParameter(string n, DbParameter v) =>
            _items[IndexOf(n)] = (FakeParameter)v;

        public Dictionary<string, object?> ToDictionary() =>
            _items.ToDictionary(p => p.ParameterName, p => p.Value);
    }

    // ── Helpers de construcción ───────────────────────────────────────────────

    /// <summary>
    /// El interceptor nunca accede al eventData, así que null! es seguro en tests.
    /// ConnectionEndEventData tiene un constructor complejo (8 parámetros internos de EF Core)
    /// y no es mockeable con NSubstitute sin pasar todos esos argumentos; null! evita esa dependencia.
    /// </summary>
    private static ConnectionEndEventData FakeEventData() => null!;

    private static (TenantDbContextInterceptor interceptor, FakeConnection conn) Build(
        ITenantContext tenantCtx)
    {
        var interceptor = new TenantDbContextInterceptor(
            tenantCtx,
            NullLogger<TenantDbContextInterceptor>.Instance);
        var conn = new FakeConnection();
        return (interceptor, conn);
    }

    private static ITenantContext InitializedCtx(
        Guid tenantId, string role = "admin", Guid? userId = null)
    {
        var ctx = new TenantContext();
        ctx.Initialize(tenantId, role, userId);
        return ctx;
    }

    private static ITenantContext UninitializedCtx() => new TenantContext();

    // ── Contexto no inicializado → no se ejecuta ningún comando ──────────────

    [Fact]
    public void ConnectionOpened_UninitializedContext_ExecutesNoCommand()
    {
        var (interceptor, conn) = Build(UninitializedCtx());

        interceptor.ConnectionOpened(conn, FakeEventData());

        conn.ExecutedCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task ConnectionOpenedAsync_UninitializedContext_ExecutesNoCommand()
    {
        var (interceptor, conn) = Build(UninitializedCtx());

        await interceptor.ConnectionOpenedAsync(conn, FakeEventData());

        conn.ExecutedCommands.Should().BeEmpty();
    }

    // ── Contexto inicializado → claim_tenant_context() ejecutado ─────────────

    [Fact]
    public void ConnectionOpened_InitializedContext_ExecutesClaimSql()
    {
        var tenantId = Guid.NewGuid();
        var userId   = Guid.NewGuid();
        var (interceptor, conn) = Build(InitializedCtx(tenantId, "admin", userId));

        interceptor.ConnectionOpened(conn, FakeEventData());

        conn.ExecutedCommands.Should().HaveCount(1);
        conn.ExecutedCommands[0].sql.Should().Contain("claim_tenant_context");
    }

    [Fact]
    public async Task ConnectionOpenedAsync_InitializedContext_ExecutesClaimSql()
    {
        var tenantId = Guid.NewGuid();
        var (interceptor, conn) = Build(InitializedCtx(tenantId));

        await interceptor.ConnectionOpenedAsync(conn, FakeEventData());

        conn.ExecutedCommands.Should().HaveCount(1);
        conn.ExecutedCommands[0].sql.Should().Contain("claim_tenant_context");
    }

    // ── Parámetros SQL correctos ──────────────────────────────────────────────

    [Fact]
    public void ConnectionOpened_PassesCorrectParameters()
    {
        var tenantId = Guid.NewGuid();
        var userId   = Guid.NewGuid();
        var (interceptor, conn) = Build(InitializedCtx(tenantId, "therapist", userId));

        interceptor.ConnectionOpened(conn, FakeEventData());

        var p = conn.ExecutedCommands[0].@params;
        p["tenant_id"].Should().Be(tenantId);
        p["user_role"].Should().Be("therapist");
        p["user_id"].Should().Be(userId);
    }

    [Fact]
    public void ConnectionOpened_WhenNoRole_UsesServiceDefault()
    {
        // Contexto con rol nulo (internalizado como null en TenantContext)
        var tenantCtx = Substitute.For<ITenantContext>();
        tenantCtx.IsInitialized.Returns(true);
        tenantCtx.TenantId.Returns(Guid.NewGuid());
        tenantCtx.UserRole.Returns((string?)null);
        tenantCtx.UserId.Returns((Guid?)null);

        var (interceptor, conn) = Build(tenantCtx);
        interceptor.ConnectionOpened(conn, FakeEventData());

        var p = conn.ExecutedCommands[0].@params;
        p["user_role"].Should().Be("service", "el rol por defecto cuando es null es 'service'");
    }

    [Fact]
    public void ConnectionOpened_WhenNoUserId_PassesDBNull()
    {
        var tenantCtx = Substitute.For<ITenantContext>();
        tenantCtx.IsInitialized.Returns(true);
        tenantCtx.TenantId.Returns(Guid.NewGuid());
        tenantCtx.UserRole.Returns("admin");
        tenantCtx.UserId.Returns((Guid?)null);

        var (interceptor, conn) = Build(tenantCtx);
        interceptor.ConnectionOpened(conn, FakeEventData());

        var p = conn.ExecutedCommands[0].@params;
        p["user_id"].Should().Be(DBNull.Value);
    }

    // ── Error en Postgres → excepción relanzada ───────────────────────────────

    // ── Error en Postgres → excepción relanzada ───────────────────────────────

    /// <summary>
    /// DbConnection independiente cuyo CreateDbCommand() devuelve un comando
    /// que falla en ExecuteNonQuery con "Postgres error".
    /// Hereda de DbConnection (no de FakeConnection, que es sealed) para poder
    /// sobrescribir CreateDbCommand() correctamente.
    /// </summary>
    private sealed class FailingConnection : DbConnection
    {
        protected override DbTransaction BeginDbTransaction(IsolationLevel il) =>
            throw new NotSupportedException();
        public override void ChangeDatabase(string name) => throw new NotSupportedException();
        public override void Close()  { }
        public override void Open()   { }
        public override Task OpenAsync(CancellationToken ct) => Task.CompletedTask;
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database         => "test";
        public override string DataSource       => "test";
        public override string ServerVersion    => "test";
        public override ConnectionState State   => ConnectionState.Open;
        protected override DbCommand CreateDbCommand()  => new FailingCommand();
    }

    private sealed class FailingCommand : DbCommand
    {
        private readonly FakeParameterCollection _params = new();
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _params;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new Exception("Postgres error");
        public override Task<int> ExecuteNonQueryAsync(CancellationToken ct) =>
            throw new Exception("Postgres error");
        public override object? ExecuteScalar() => null;
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior b) =>
            throw new NotSupportedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new FakeParameter();
    }

    [Fact]
    public void ConnectionOpened_WhenCommandFails_ThrowsInvalidOperationException()
    {
        var tenantCtx = Substitute.For<ITenantContext>();
        tenantCtx.IsInitialized.Returns(true);
        tenantCtx.TenantId.Returns(Guid.NewGuid());
        tenantCtx.UserRole.Returns("admin");
        tenantCtx.UserId.Returns((Guid?)null);

        var conn = new FailingConnection();

        var interceptor = new TenantDbContextInterceptor(
            tenantCtx,
            NullLogger<TenantDbContextInterceptor>.Instance);

        var act = () => interceptor.ConnectionOpened(conn, FakeEventData());

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*claim_tenant_context*")
           .WithInnerException<Exception>()
           .WithMessage("Postgres error");
    }
}
