using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
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

    private enum GridMode
    {
        Locked,
        Scale
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
            Func<CameraFilterValues, bool> isEnabledPredicate,
            string gridArea)
        {
            Tile = tile;
            Label = label;
            DataCamera = dataCamera;
            VideoKey = videoKey;
            _segmentSelector = segmentSelector;
            _isEnabledPredicate = isEnabledPredicate;
            GridArea = gridArea;
        }

        public Tile Tile { get; }
        public string Label { get; }
        public string DataCamera { get; }
        public string VideoKey { get; }
        public VideoPlayer Player { get; set; }
        public string GridArea { get; }
        public ElementReference ElementRef { get; set; }

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
            new TileDefinition(Tile.LeftPillar, "Left Pillar", "left-pillar", "L-BPILLAR", segment => segment?.CameraLeftBPillar, filter => filter.ShowLeftPillar, "left-pillar"),
            new TileDefinition(Tile.Front, "Front", "front", "128D7AB3", segment => segment?.CameraFront, filter => filter.ShowFront, "front"),
            new TileDefinition(Tile.RightPillar, "Right Pillar", "right-pillar", "R-BPILLAR", segment => segment?.CameraRightBPillar, filter => filter.ShowRightPillar, "right-pillar"),
            new TileDefinition(Tile.LeftRepeater, "Left Repeater", "left-repeater", "D1916B24", segment => segment?.CameraLeftRepeater, filter => filter.ShowLeftRepeater, "left-repeater"),
            new TileDefinition(Tile.Back, "Back", "back", "66EC38D4", segment => segment?.CameraBack, filter => filter.ShowBack, "back"),
            new TileDefinition(Tile.RightRepeater, "Right Repeater", "right-repeater", "87B15DCA", segment => segment?.CameraRightRepeater, filter => filter.ShowRightRepeater, "right-repeater")
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
        if (IsGridLocked)
        {
            return "grid-template-columns: repeat(3, minmax(0, 1fr)); grid-template-rows: repeat(2, minmax(0, 1fr)); grid-template-areas: \"left-pillar front right-pillar\" \"left-repeater back right-repeater\"; grid-auto-rows: minmax(0, 1fr);";
        }

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

    private bool IsGridLocked => _gridMode == GridMode.Locked;

    private string GetTileStyle(TileDefinition tile)
    {
        if (!IsGridLocked || string.IsNullOrWhiteSpace(tile.GridArea))
        {
            return null;
        }

        if (_fullscreenTile == tile.Tile)
        {
            return null;
        }

        return $"grid-area: {tile.GridArea};";
    }

    private async Task ToggleGridMode()
    {
        _gridMode = _gridMode == GridMode.Locked ? GridMode.Scale : GridMode.Locked;

        await InvokeAsync(StateHasChanged);
    }
}
