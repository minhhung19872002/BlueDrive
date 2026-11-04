using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BlueDrive.Application.Features.Access;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BlueDrive.Infrastructure.Persistence;

/// <summary>
/// Bumps <see cref="PermissionGeneration"/> on every write to a permission-relevant table — SaveChanges, ExecuteUpdate / ExecuteDelete and
/// raw SQL alike — and again when a transaction holding such a write commits (a decision read before the commit must not stay cached).
/// </summary>
public sealed partial class PermissionGenerationInterceptor : DbCommandInterceptor
{
    public static readonly PermissionGenerationInterceptor Instance = new();

    private static readonly ConditionalWeakTable<DbTransaction, object> Dirty = new();

    [GeneratedRegex(@"\b(?:insert\s+into|update|delete\s+from|merge\s+into|truncate(?:\s+table)?)\s+(?:only\s+)?(?:""?public""?\.)?""?([A-Za-z_]+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex WriteTarget();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex WordBoundary();

    /// <summary>"acl_entries", "AclEntries" (a context without the snake_case convention) → "acl_entries".</summary>
    private static string Table(string name) => WordBoundary().Replace(name, "$1_$2").ToLowerInvariant();

    private static void Seen(DbCommand command)
    {
        foreach (Match m in WriteTarget().Matches(command.CommandText))
            if (PermissionGeneration.Tables.Contains(Table(m.Groups[1].Value)))
            {
                PermissionGeneration.Bump();
                if (command.Transaction is { } tx) Dirty.AddOrUpdate(tx, true);
                return;
            }
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Seen(command);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Seen(command);
        return ValueTask.FromResult(result);
    }

    // SaveChanges sends INSERT ... RETURNING / batches through readers.
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Seen(command);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Seen(command);
        return ValueTask.FromResult(result);
    }

    /// <summary>The commit side: registered next to the command interceptor.</summary>
    public static readonly DbTransactionInterceptor Transactions = new TransactionPart();

    private sealed class TransactionPart : DbTransactionInterceptor
    {
        private static void Committed(DbTransaction transaction)
        {
            if (Dirty.TryGetValue(transaction, out _))
            {
                Dirty.Remove(transaction);
                PermissionGeneration.Bump();
            }
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Committed(transaction);

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Committed(transaction);
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Dirty.Remove(transaction);

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Dirty.Remove(transaction);
            return Task.CompletedTask;
        }
    }
}
