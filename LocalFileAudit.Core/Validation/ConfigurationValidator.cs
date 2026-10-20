using LocalFileAudit.Core.Configuration;

namespace LocalFileAudit.Core.Validation;

/// <summary>
/// Validator for application configuration enforcing safety and operational invariants.
/// </summary>
public interface IConfigurationValidator
{
    ValidationResult Validate(AppConfiguration config);
}

public sealed class ConfigurationValidator : IConfigurationValidator
{
    public ValidationResult Validate(AppConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<string>();

        // 1. Absolute safety rules (Specification Section 5 & 13)
        if (config.Safety.AutomaticDeletionEnabled)
        {
            errors.Add("Safety violation: AutomaticDeletionEnabled is permanently unsupported and must be false.");
        }

        if (config.Safety.AutomaticMoveEnabled)
        {
            errors.Add("Safety violation: AutomaticMoveEnabled is permanently unsupported and must be false.");
        }

        if (config.Safety.AutomaticCopyEnabled)
        {
            errors.Add("Safety violation: AutomaticCopyEnabled is permanently unsupported and must be false.");
        }

        // 2. Scanning bounds
        if (config.Scanning.MaxConcurrentDirectoryWorkers < 1)
        {
            errors.Add("Scanning.MaxConcurrentDirectoryWorkers must be at least 1.");
        }
        else if (config.Scanning.MaxConcurrentDirectoryWorkers > 16)
        {
            errors.Add("Scanning.MaxConcurrentDirectoryWorkers cannot exceed 16.");
        }

        if (config.Scanning.DatabaseBatchSize < 10)
        {
            errors.Add("Scanning.DatabaseBatchSize must be at least 10.");
        }
        else if (config.Scanning.DatabaseBatchSize > 50000)
        {
            errors.Add("Scanning.DatabaseBatchSize cannot exceed 50000.");
        }

        // 3. Analysis bounds
        if (config.Analysis.LargeFileThresholdBytes <= 0)
        {
            errors.Add("Analysis.LargeFileThresholdBytes must be greater than 0.");
        }

        if (config.Analysis.OldFileAgeDays < 0)
        {
            errors.Add("Analysis.OldFileAgeDays cannot be negative.");
        }

        // 4. AI endpoint safety
        if (config.AI.RequireLocalEndpoint && !string.IsNullOrWhiteSpace(config.AI.Endpoint))
        {
            if (Uri.TryCreate(config.AI.Endpoint, UriKind.Absolute, out var uri))
            {
                var isLoopback = uri.IsLoopback ||
                                 string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);

                if (!isLoopback)
                {
                    errors.Add($"AI safety violation: AI endpoint '{config.AI.Endpoint}' is not a local loopback address, but RequireLocalEndpoint is enabled.");
                }
            }
            else
            {
                errors.Add($"AI endpoint '{config.AI.Endpoint}' is not a valid absolute URI.");
            }
        }

        if (config.AI.TimeoutSeconds < 1 || config.AI.TimeoutSeconds > 600)
        {
            errors.Add("AI.TimeoutSeconds must be between 1 and 600 seconds.");
        }

        if (config.AI.MaxPayloadCandidates < 1 || config.AI.MaxPayloadCandidates > 1000)
        {
            errors.Add("AI.MaxPayloadCandidates must be between 1 and 1000.");
        }

        return errors.Count == 0 ? ValidationResult.Success() : ValidationResult.Failure(errors);
    }
}
