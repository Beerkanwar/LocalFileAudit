using System.Data.Common;
using LocalFileAudit.Core.Common;
using LocalFileAudit.Core.Models;
using Microsoft.Data.Sqlite;

namespace LocalFileAudit.Infrastructure.Persistence;

/// <summary>
/// Unified, high-performance mapper for translating SQLite rows into FileRecord instances.
/// Eliminates duplication across SqliteScanRepository, StorageAnalysisEngine, and DuplicateAnalysisEngine.
/// </summary>
public static class FileRecordMapper
{
    public static FileRecord Map(SqliteDataReader reader)
    {
        return new FileRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            ScanRunId = Guid.Parse(reader.GetString(reader.GetOrdinal("ScanRunId"))),
            FullPath = reader.GetString(reader.GetOrdinal("FullPath")),
            ParentPath = reader.GetString(reader.GetOrdinal("ParentPath")),
            FileName = reader.GetString(reader.GetOrdinal("FileName")),
            Extension = reader.GetString(reader.GetOrdinal("Extension")),
            SizeBytes = reader.GetInt64(reader.GetOrdinal("SizeBytes")),
            CreationTimeUtc = reader.IsDBNull(reader.GetOrdinal("CreationTimeUtc")) ? null : UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("CreationTimeUtc"))),
            LastWriteTimeUtc = reader.IsDBNull(reader.GetOrdinal("LastWriteTimeUtc")) ? null : UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("LastWriteTimeUtc"))),
            LastAccessTimeUtc = reader.IsDBNull(reader.GetOrdinal("LastAccessTimeUtc")) ? null : UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("LastAccessTimeUtc"))),
            Attributes = reader.GetInt32(reader.GetOrdinal("Attributes")),
            ObservedAtUtc = UtcDateTimeHelper.ParseUtc(reader.GetString(reader.GetOrdinal("ObservedAtUtc"))),
            MetadataStatus = Enum.TryParse<ScanMetadataStatus>(reader.GetString(reader.GetOrdinal("MetadataStatus")), ignoreCase: true, out var ms) ? ms : ScanMetadataStatus.Ok,
            HashAlgorithm = reader.IsDBNull(reader.GetOrdinal("HashAlgorithm")) ? null : reader.GetString(reader.GetOrdinal("HashAlgorithm")),
            HashValue = reader.IsDBNull(reader.GetOrdinal("HashValue")) ? null : reader.GetString(reader.GetOrdinal("HashValue"))
        };
    }
}
