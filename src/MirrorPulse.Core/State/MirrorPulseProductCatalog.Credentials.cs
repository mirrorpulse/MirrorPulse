using System.Globalization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    internal async Task ScheduleCredentialCleanupAsync(CredentialReference reference,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO credential_cleanup (reference_id, provider, created_utc)
                VALUES ($id, $provider, $created);
                """;
            command.Parameters.AddWithValue("$id", reference.ReferenceId);
            command.Parameters.AddWithValue("$provider", reference.Provider);
            command.Parameters.AddWithValue("$created", reference.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task<IReadOnlyList<CredentialReference>> ReadCredentialCleanupAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "SELECT reference_id, provider, created_utc FROM credential_cleanup;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<CredentialReference>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(new(reader.GetString(0), CredentialKind.Password, reader.GetString(1),
                    CredentialScope.CurrentUser, DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
            }

            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task CompleteCredentialCleanupAsync(string referenceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM credential_cleanup WHERE reference_id = $id;";
            command.Parameters.AddWithValue("$id", referenceId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
