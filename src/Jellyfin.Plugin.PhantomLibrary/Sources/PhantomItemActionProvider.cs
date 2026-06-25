using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.PhantomLibrary.Sources;

/// <summary>
/// Exposes Phantom item operations through Jellyfin's server-advertised item action API.
/// </summary>
public sealed class PhantomItemActionProvider : IItemActionProvider
{
    public const string MaterialiseActionId = "phantom.materialise";
    public const string ResetActionId = "phantom.reset";
    public const string RejectCurrentActionId = "phantom.rejectCurrent";
    public const string MaterialiseCandidateActionPrefix = "phantom.materialiseCandidate.";

    private readonly PhantomSourceManager _sourceManager;
    private readonly IMaterialiser _materialiser;

    public PhantomItemActionProvider(PhantomSourceManager sourceManager, IMaterialiser materialiser)
    {
        _sourceManager = sourceManager ?? throw new ArgumentNullException(nameof(sourceManager));
        _materialiser = materialiser ?? throw new ArgumentNullException(nameof(materialiser));
    }

    public async Task<IReadOnlyList<ItemActionInfo>> GetActionsAsync(BaseItem item, User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        _ = user;

        if (!IsPhantomChannelItem(item) || string.IsNullOrWhiteSpace(item.ExternalId))
        {
            return Array.Empty<ItemActionInfo>();
        }

        var sources = await _sourceManager.GetSourcesAsync(item.ExternalId, cancellationToken).ConfigureAwait(false);
        if (sources is null)
        {
            return Array.Empty<ItemActionInfo>();
        }

        var actions = new List<ItemActionInfo>();
        var materialising = string.Equals(sources.Status, "materialising", StringComparison.OrdinalIgnoreCase);
        var materialised = string.Equals(sources.Status, "materialised", StringComparison.OrdinalIgnoreCase);
        var unavailable = string.Equals(sources.Status, "unavailable", StringComparison.OrdinalIgnoreCase);

        if (!materialised)
        {
            actions.Add(new ItemActionInfo
            {
                Id = MaterialiseActionId,
                Name = materialising ? "Materialising…" : "Materialise Phantom",
                Description = materialising
                    ? "Phantom Library is already materialising this item."
                    : "Materialise this Phantom item through the configured source picker.",
                Icon = "play_arrow",
                IsEnabled = !materialising && !unavailable,
                RefreshItemAfterInvoke = true,
            });

            foreach (var candidate in sources.Candidates.Where(c => !c.IsRejected).Take(5))
            {
                actions.Add(new ItemActionInfo
                {
                    Id = CandidateActionId(candidate),
                    Name = "Materialise: " + CandidateLabel(candidate),
                    Description = "Materialise this Phantom item using the selected source candidate.",
                    Icon = "cloud_download",
                    IsEnabled = !materialising,
                    RequiresConfirmation = true,
                    ConfirmationText = "Materialise using this selected Phantom source?",
                    RefreshItemAfterInvoke = true,
                });
            }
        }

        if (materialised || unavailable)
        {
            actions.Add(new ItemActionInfo
            {
                Id = ResetActionId,
                Name = "Reset Phantom",
                Description = "Clear Phantom materialisation state and return this item to the base available Phantom state.",
                Icon = "restart_alt",
                IsEnabled = !materialising,
                RequiresConfirmation = true,
                ConfirmationText = "Reset Phantom state for this item? This does not reject the current source.",
                RefreshItemAfterInvoke = true,
            });
        }

        if (sources.CanRejectCurrent)
        {
            actions.Add(new ItemActionInfo
            {
                Id = RejectCurrentActionId,
                Name = "Reject current Phantom source",
                Description = "Reject the current source and try an alternate candidate if one exists.",
                Icon = "block",
                IsEnabled = !materialising,
                RequiresConfirmation = true,
                ConfirmationText = "Reject this source? Phantom Library will avoid it when selecting future candidates.",
                RefreshItemAfterInvoke = true,
            });
        }

        return actions;
    }

    public async Task<ItemActionResult> InvokeAsync(BaseItem item, User user, string actionId, ItemActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        _ = user;
        _ = request;

        if (!IsPhantomChannelItem(item) || string.IsNullOrWhiteSpace(item.ExternalId))
        {
            return Failure("not_found", "Phantom channel external id not found");
        }

        switch (actionId)
        {
            case MaterialiseActionId:
                var outcome = await _materialiser.MaterialiseAsync(item.Id, MaterialiseTrigger.Manual, cancellationToken).ConfigureAwait(false);
                return FromMaterialisationOutcome(outcome);
            case ResetActionId:
                return FromSourceResult(await _sourceManager.ResetCurrentAsync(item.ExternalId, cancellationToken).ConfigureAwait(false));
            case RejectCurrentActionId:
                return FromSourceResult(await _sourceManager.RejectCurrentAsync(item.ExternalId, cancellationToken).ConfigureAwait(false));
            default:
                if (actionId.StartsWith(MaterialiseCandidateActionPrefix, StringComparison.Ordinal))
                {
                    var candidate = await ResolveCandidateRequestAsync(item.ExternalId, actionId, request, cancellationToken).ConfigureAwait(false);
                    if (candidate is null)
                    {
                        return Failure("candidate_not_found", "Selected Phantom source candidate was not found");
                    }

                    return FromSourceResult(await _sourceManager.MaterialiseCandidateAsync(item.ExternalId, candidate, cancellationToken).ConfigureAwait(false));
                }

                return Failure("action_not_found", "Action not found");
        }
    }

    private static bool IsPhantomChannelItem(BaseItem item)
        => item.SourceType == SourceType.Channel || ChannelIds.IsPhantom(item.ChannelId);

    private async Task<PhantomMaterialiseCandidateRequest?> ResolveCandidateRequestAsync(string externalId, string actionId, ItemActionRequest request, CancellationToken cancellationToken)
    {
        var sources = await _sourceManager.GetSourcesAsync(externalId, cancellationToken).ConfigureAwait(false);
        var advertised = sources?.Candidates.FirstOrDefault(c => string.Equals(CandidateActionId(c), actionId, StringComparison.Ordinal));
        if (advertised is null)
        {
            return null;
        }

        var payloadCandidate = TryReadCandidatePayload(request);
        if (payloadCandidate is not null && !string.Equals(payloadCandidate.Magnet, advertised.Magnet, StringComparison.Ordinal))
        {
            return null;
        }

        return ToCandidateRequest(advertised);
    }

    private static PhantomMaterialiseCandidateRequest? TryReadCandidatePayload(ItemActionRequest request)
    {
        if (request.Payload is null)
        {
            return null;
        }

        var magnet = ReadString(request, "magnet") ?? ReadString(request, "Magnet");
        if (string.IsNullOrWhiteSpace(magnet))
        {
            return null;
        }

        return new PhantomMaterialiseCandidateRequest
        {
            Magnet = magnet,
            InfoHash = ReadString(request, "infoHash") ?? ReadString(request, "InfoHash"),
            Indexer = ReadString(request, "indexer") ?? ReadString(request, "Indexer"),
            Title = ReadString(request, "title") ?? ReadString(request, "Title"),
            Size = ReadLong(request, "size") ?? ReadLong(request, "Size"),
            Seeders = ReadInt(request, "seeders") ?? ReadInt(request, "Seeders"),
            OverrideRejected = ReadBool(request, "overrideRejected") ?? ReadBool(request, "OverrideRejected") ?? false,
        };
    }

    private static string? ReadString(ItemActionRequest request, string name)
        => request.Payload is not null && request.Payload.TryGetPropertyValue(name, out var node) ? node?.GetValue<string>() : null;

    private static long? ReadLong(ItemActionRequest request, string name)
        => request.Payload is not null && request.Payload.TryGetPropertyValue(name, out var node) && node is not null ? node.GetValue<long>() : null;

    private static int? ReadInt(ItemActionRequest request, string name)
        => request.Payload is not null && request.Payload.TryGetPropertyValue(name, out var node) && node is not null ? node.GetValue<int>() : null;

    private static bool? ReadBool(ItemActionRequest request, string name)
        => request.Payload is not null && request.Payload.TryGetPropertyValue(name, out var node) && node is not null ? node.GetValue<bool>() : null;

    private static PhantomMaterialiseCandidateRequest ToCandidateRequest(PhantomSourceCandidateDto candidate)
        => new()
        {
            Magnet = candidate.Magnet,
            InfoHash = candidate.InfoHash,
            Indexer = candidate.Indexer,
            Title = candidate.Title,
            Size = candidate.Size,
            Seeders = candidate.Seeders,
        };

    private static string CandidateActionId(PhantomSourceCandidateDto candidate)
        => MaterialiseCandidateActionPrefix
            + candidate.Rank.ToString(CultureInfo.InvariantCulture)
            + "."
            + candidate.InfoHash;

    private static string CandidateLabel(PhantomSourceCandidateDto candidate)
    {
        var title = string.IsNullOrWhiteSpace(candidate.Title) ? candidate.Indexer ?? candidate.InfoHash : candidate.Title;
        var parts = new List<string> { title ?? candidate.InfoHash };
        if (!string.IsNullOrWhiteSpace(candidate.Indexer))
        {
            parts.Add(candidate.Indexer);
        }

        if (candidate.Seeders.HasValue)
        {
            parts.Add(candidate.Seeders.Value.ToString(CultureInfo.InvariantCulture) + " seeders");
        }

        return string.Join(" · ", parts);
    }

    private static ItemActionResult FromMaterialisationOutcome(MaterialisationOutcome outcome)
    {
        var success = outcome.Status is MaterialisationStatus.Success or MaterialisationStatus.Duplicate;
        return new ItemActionResult
        {
            Success = success,
            Code = outcome.Status switch
            {
                MaterialisationStatus.Success => "success",
                MaterialisationStatus.Duplicate => "duplicate",
                MaterialisationStatus.Unavailable => "unavailable",
                MaterialisationStatus.AlreadyInProgress => "in_flight",
                _ => "error",
            },
            Message = success ? "Phantom item materialised" : outcome.Error ?? "Materialise failed",
            RefreshItem = true,
        };
    }

    private static ItemActionResult FromSourceResult(PhantomSourceOperationResult result)
        => new()
        {
            Success = result.Status == PhantomSourceOperationStatus.Success,
            Code = result.Code,
            Message = result.Message,
            RefreshItem = true,
        };

    private static ItemActionResult Failure(string code, string message)
        => new()
        {
            Success = false,
            Code = code,
            Message = message,
            RefreshItem = false,
        };
}
