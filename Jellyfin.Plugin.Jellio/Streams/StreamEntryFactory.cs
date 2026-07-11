using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellio.Helpers;
using Jellyfin.Plugin.Jellio.Models;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Jellyfin.Plugin.Jellio.Streams;

/// <summary>
/// Builds Stremio stream entries for a Jellyfin MediaSource. Jellyfin's master.m3u8 advertises an SDR H.264 fallback variant on HDR sources, and Stremio's HLS engine can pick that fallback over the HDR primary, leaving the user with the lower-quality transcode instead of direct-play HDR. To prevent that, an HDR source whose codec is in the declared list gets two entries (a "Direct Play" entry pinned to the single-variant main.m3u8, plus an "Adaptive" entry on master.m3u8), while all other sources get a single master.m3u8 entry.
/// </summary>
public static class StreamEntryFactory
{
    private const string MasterEndpoint = "master.m3u8";
    private const string MainEndpoint = "main.m3u8";
    private const string AdaptivePrefix = "Adaptive";
    private const string AdaptiveSeparator = " · ";
    private const string SuffixSeparator = " — ";

    /*
     * Stremio addons can't probe client codec support, so the lists below are hardcoded to what Stremio's players can decode. Jellyfin's HLS endpoint compares them against the source's codecs to decide between direct-stream and transcode; without them, Jellyfin falls back to "m3u8" as the audio codec name and produces invalid FFmpeg commands.
     * See: https://github.com/jellyfin/jellyfin/issues/12926
     *
     * Each codec must be sent as its own repeated query parameter (e.g. videoCodec=h264&videoCodec=hevc)
     * so Jellyfin binds them into a string[] on StreamingRequestDto.SupportedVideoCodecs. A single
     * comma-joined value is accepted by the parameter regex but is treated as one literal codec name
     * later in the pipeline, which can force a doomed transcode attempt and a 404 to the player.
     */
    private static readonly IReadOnlyList<string> DeclaredVideoCodecs =
        ["h264", "hevc", "av1"];

    private static readonly IReadOnlyList<string> DeclaredAudioCodecs =
        ["aac", "mp3", "ac3", "eac3", "flac", "opus"];

    public static IReadOnlyList<StreamDto> Build(StreamEntryRequest request)
    {
        var videoStream = PickVideoStream(request.MediaSource);
        if (PredictsHdrFallback(videoStream))
        {
            var suffix = ResolveSuffix(request);
            var directDescription = StreamLabelFormatter.FormatDirectDescription(
                request.MediaSource,
                videoStream!,
                PickAudioStream(request.MediaSource)) + suffix;
            var autoDescription = FormatAdaptiveDescription(request);
            return
            [
                BuildEntry(request, MainEndpoint, directDescription),
                BuildEntry(request, MasterEndpoint, autoDescription),
            ];
        }

        return [BuildEntry(request, MasterEndpoint, request.MediaSource.Name ?? string.Empty)];
    }

    private static string ResolveSuffix(StreamEntryRequest request)
    {
        if (!request.IsMultiMediaSource)
        {
            return string.Empty;
        }
        var label = ResolveSourceLabel(request);
        return label is null ? string.Empty : SuffixSeparator + label;
    }

    private static string FormatAdaptiveDescription(StreamEntryRequest request)
    {
        var label = ResolveSourceLabel(request);
        return label is null ? AdaptivePrefix : AdaptivePrefix + AdaptiveSeparator + label;
    }

    private static string? ResolveSourceLabel(StreamEntryRequest request)
    {
        if (!string.IsNullOrEmpty(request.MediaSource.Name))
        {
            return request.MediaSource.Name;
        }
        return request.IsMultiMediaSource
            ? "#" + request.SourceIndex.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static MediaStream? PickVideoStream(MediaSourceInfo source) =>
        (source.MediaStreams ?? []).FirstOrDefault(stream => stream.Type == MediaStreamType.Video);

    internal static MediaStream? PickAudioStream(MediaSourceInfo source)
    {
        var audioStreams = (source.MediaStreams ?? [])
            .Where(stream => stream.Type == MediaStreamType.Audio)
            .ToList();
        return audioStreams.FirstOrDefault(stream => stream.IsDefault) ?? audioStreams.FirstOrDefault();
    }

    private static bool PredictsHdrFallback(MediaStream? videoStream)
    {
        if (videoStream is null || videoStream.VideoRange != VideoRange.HDR)
        {
            return false;
        }

        return DeclaredVideoCodecs.Contains(videoStream.Codec, StringComparer.OrdinalIgnoreCase);
    }

    private static StreamDto BuildEntry(StreamEntryRequest request, string endpoint, string description)
    {
        var path = request.MediaSource.Path;
        return new StreamDto
        {
            Url = BuildUrl(request, endpoint),
            Name = "Jellio++",
            Description = description,
            BehaviorHints = new BehaviorHintsDto
            {
                Filename = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path),
                VideoSize = request.MediaSource.Size,
                VideoHash = OpenSubtitlesHash.ComputeFromPath(path),
                NotWebReady = true,
            },
        };
    }

    private static string BuildUrl(StreamEntryRequest request, string endpoint)
    {
        // Jellyfin's DynamicHlsController tracks active encodings by deviceId+playSessionId and
        // returns 404 if the request can't be associated with an existing device/session. Stremio
        // doesn't expose a client device id, so we derive a stable one from the auth token and
        // generate a fresh playSessionId per stream so ExoPlayer-based clients (Nuvio Android TV,
        // etc.) receive a usable HLS manifest instead of an unrecoverable 404.
        var deviceId = DeriveDeviceId(request.AuthToken);
        var playSessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        var parameters = new List<KeyValuePair<string, string?>>
        {
            new("mediaSourceId", request.MediaSource.Id),
            new("api_key", request.AuthToken),
            new("deviceId", deviceId),
            new("playSessionId", playSessionId),
        };
        foreach (var codec in DeclaredVideoCodecs)
        {
            parameters.Add(new KeyValuePair<string, string?>("videoCodec", codec));
        }

        foreach (var codec in DeclaredAudioCodecs)
        {
            parameters.Add(new KeyValuePair<string, string?>("audioCodec", codec));
        }

        var query = QueryString.Empty;
        var builder = new StringBuilder();
        foreach (var (key, value) in parameters)
        {
            if (value is null)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
        }

        if (builder.Length > 0)
        {
            query = new QueryString("?" + builder.ToString());
        }

        return $"{request.BaseUrl}/Videos/{request.ItemId}/{endpoint}{query}";
    }

    private static string DeriveDeviceId(string authToken)
    {
        // Stremio doesn't pass a client device id, so produce a stable hex digest derived from the
        // auth token. This gives the HLS endpoint a real deviceId for session tracking while
        // staying deterministic for the same token (so reconnects resume cleanly).
        var inputBytes = Encoding.UTF8.GetBytes(authToken ?? string.Empty);
        var hash = SHA1.HashData(inputBytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
