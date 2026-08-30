using System.Diagnostics;
using System.Runtime.CompilerServices;
using KurrentDB.Diagnostics;
using KurrentDB.Diagnostics.Telemetry;

namespace KurrentDB.Client.Diagnostics;

static class ActivityTagsCollectionExtensions {
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static ActivityTagsCollection WithGrpcChannelServerTags(this ActivityTagsCollection tags, ChannelInfo? channelInfo) {
		if (channelInfo is null)
			return tags;

		var authorityParts = channelInfo.Channel.Target.Split(':');

		tags = tags.WithRequiredTag(TelemetryAttributes.ServerAddress, authorityParts[0]);

		if (authorityParts.Length > 1)
			tags = tags.WithRequiredTag(TelemetryAttributes.ServerPort, int.Parse(authorityParts[1]));

		return tags;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static ActivityTagsCollection WithClientSettingsServerTags(this ActivityTagsCollection source, KurrentDBClientSettings settings) {
		if (settings.ConnectivitySettings.DnsGossipSeeds?.Length != 1)
			return source;

		var gossipSeed = settings.ConnectivitySettings.DnsGossipSeeds[0];

		return source
			.WithRequiredTag(TelemetryAttributes.ServerAddress, gossipSeed.Host)
			.WithRequiredTag(TelemetryAttributes.ServerPort, gossipSeed.Port);
	}
}
