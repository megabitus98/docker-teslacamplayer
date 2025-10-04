using System;
using System.Collections.Generic;
using System.Linq;
using TeslaCamPlayer.BlazorHosted.Client.Models;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Client.Components;

public partial class ClipViewer
{
    private enum Tile
    {
        Front,
        Back,
        LeftRepeater,
        RightRepeater,
        LeftPillar,
        RightPillar
    }

    private sealed class TileDefinition
    {
        private readonly Func<ClipVideoSegment, VideoFile> _segmentSelector;
        private readonly Func<CameraFilterValues, bool> _isEnabledPredicate;

        public TileDefinition(
            Tile tile,
            string label,
            string dataCamera,
            string videoKey,
            Func<ClipVideoSegment, VideoFile> segmentSelector,
            Func<CameraFilterValues, bool> isEnabledPredicate)
        {
            Tile = tile;
            Label = label;
            DataCamera = dataCamera;
            VideoKey = videoKey;
            _segmentSelector = segmentSelector;
            _isEnabledPredicate = isEnabledPredicate;
        }

        public Tile Tile { get; }
        public string Label { get; }
        public string DataCamera { get; }
        public string VideoKey { get; }
        public VideoPlayer Player { get; set; }

        public string SourceFor(ClipVideoSegment segment)
            => _segmentSelector?.Invoke(segment)?.Url;

        public bool IsEnabled(CameraFilterValues filter)
            => _isEnabledPredicate?.Invoke(filter) ?? true;
    }

    private readonly TileDefinition[] _tiles;
    private readonly Dictionary<Tile, TileDefinition> _tileLookup;

    private static readonly Tile[] TimeSourcePriority =
    {
        Tile.Front,
        Tile.Back,
        Tile.LeftRepeater,
        Tile.RightRepeater,
        Tile.LeftPillar,
        Tile.RightPillar
    };

    public ClipViewer()
    {
        _tiles = new[]
        {
            new TileDefinition(Tile.LeftPillar, "Left Pillar", "left-pillar", "L-BPILLAR", segment => segment?.CameraLeftBPillar, filter => filter.ShowLeftPillar),
            new TileDefinition(Tile.Front, "Front", "front", "128D7AB3", segment => segment?.CameraFront, filter => filter.ShowFront),
            new TileDefinition(Tile.RightPillar, "Right Pillar", "right-pillar", "R-BPILLAR", segment => segment?.CameraRightBPillar, filter => filter.ShowRightPillar),
            new TileDefinition(Tile.LeftRepeater, "Left Repeater", "left-repeater", "D1916B24", segment => segment?.CameraLeftRepeater, filter => filter.ShowLeftRepeater),
            new TileDefinition(Tile.Back, "Back", "back", "66EC38D4", segment => segment?.CameraBack, filter => filter.ShowBack),
            new TileDefinition(Tile.RightRepeater, "Right Repeater", "right-repeater", "87B15DCA", segment => segment?.CameraRightRepeater, filter => filter.ShowRightRepeater)
        };

        _tileLookup = _tiles.ToDictionary(t => t.Tile);
    }

    private bool IsTileVisible(Tile tile)
    {
        if (!_tileLookup.TryGetValue(tile, out var definition))
        {
            return false;
        }

        var hasSrc = !string.IsNullOrWhiteSpace(definition.Player?.Src);
        return definition.IsEnabled(CameraFilter) && hasSrc;
    }

    private int VisibleTileCount()
        => _tiles.Count(tile => IsTileVisible(tile.Tile));

    private string GridStyle()
    {
        var visible = VisibleTileCount();
        int cols = visible switch
        {
            >= 5 => 3,
            4 => 2,
            3 => 3,
            2 => 2,
            1 => 1,
            _ => 3
        };

        return $"grid-template-columns: repeat({cols}, minmax(0, 1fr)); grid-auto-rows: minmax(0, 1fr);";
    }
}
