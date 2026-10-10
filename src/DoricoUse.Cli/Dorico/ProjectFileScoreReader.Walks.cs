using System.Globalization;
using System.Text;

namespace DoricoUse.Cli.Dorico;

internal static partial class ProjectFileScoreReader
{
    private sealed partial class Decoder
    {
        private bool WalkInfo(long count)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "title": _info.Title = Value(value); break;
                        case "subtitle": _info.Subtitle = Value(value); break;
                        case "composer": _info.Composer = Value(value); break;
                        case "arranger": _info.Arranger = Value(value); break;
                        case "lyricist": _info.Lyricist = Value(value); break;
                        case "createdDate": _info.CreatedDate = Value(value); break;
                        case "lastSavedDate": _info.LastSavedDate = Value(value); break;
                        case "createdVersion": _info.CreatedVersion = Value(value); break;
                        case "lastSavedVersion": _info.LastSavedVersion = Value(value); break;
                        case "originator": _info.Originator = Value(value); break;
                    }

                    return true;
                },
                (_, children, _) => SkipChildren(children));

        // ------------------------------------------------------------ players, instruments, layouts

        private bool WalkLayouts(long count)
            => WalkItems(count, (_, children) =>
            {
                var layout = new LayoutDraft();

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "layoutID": layout.Id = Number(value); break;
                            case "layoutType": layout.Type = Value(value); break;
                            case "layoutName": layout.Name = Value(value); break;
                            case "layoutNumber": layout.Number = Number(value); break;
                        }

                        return true;
                    },
                    (key, childCount, _) => NameAt(key) switch
                    {
                        "layoutOptions" => WalkLayoutOptions(childCount, layout),
                        "pageSequenceOverrides" => WalkPageSequenceOverrides(childCount, layout),
                        _ => SkipChildren(childCount),
                    });

                _layouts.Add(layout);

                return ok;
            });

        /// <summary>
        /// One layout's engraving options. The page box, the casting off and the two <b>view-state</b> flags are
        /// read: the rest is the engraving and notation option tree, which <c>options get</c> reaches through
        /// Dorico itself.
        /// </summary>
        /// <remarks>
        /// Measured on a small project: <c>layoutOptions.transpositionType</c> is <c>kScoreInC</c> on its full score
        /// and <c>kTransposingScore</c> on its part layout, and toggling the layout's transposition in Dorico (the
        /// <c>transpositionType</c> option reads back as the new value live) changes exactly that field in the saved
        /// file.
        /// </remarks>
        private bool WalkLayoutOptions(long count, LayoutDraft layout)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "pagePairSetEntityID": layout.PageSet = Value(value); break;
                        case "defaultSpaceSize": layout.SpaceSize = Value(value); break;
                        case "transpositionType": layout.TranspositionType = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) switch
                {
                    "pageSizeAndMarginOptions" => WalkPageSize(childCount, layout),
                    "castingOffOptions" => WalkCastingOff(childCount, layout),
                    "condensingLayoutOptions" => WalkCondensing(childCount, layout),
                    _ => SkipChildren(childCount),
                });

        /// <summary>
        /// One layout's condensing group, whose <c>enableCondensing</c> is the stored half of "enable the optimised
        /// score" (checklist item 5).
        /// </summary>
        /// <remarks>
        /// Measured on a small project, and the measurement is the reason this is a separate walk: the flag is not a
        /// leaf of <c>layoutOptions</c> itself — that is where the first attempt looked and it always read null — but
        /// of the <c>condensingLayoutOptions</c> node inside it. Toggling the layout's condensing in Dorico (the live
        /// status then reads <c>condensingEnabled: true</c>) and saving a copy changed exactly this leaf, on that
        /// layout only: the full score went to <c>true</c> and the part layout beside it stayed <c>false</c>.
        /// </remarks>
        private bool WalkCondensing(long count, LayoutDraft layout)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    if (NameAt(key) == "enableCondensing")
                    {
                        layout.Condensing = Value(value) == "true";
                    }

                    return true;
                },
                (_, children, _) => SkipChildren(children));

        /// <summary>The page box of one layout: <c>pageWidth</c>/<c>pageHeight</c> are in points, as are the margins.</summary>
        private bool WalkPageSize(long count, LayoutDraft layout)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "pageWidth": layout.PageWidth = Value(value); break;
                        case "pageHeight": layout.PageHeight = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "margins"
                    ? WalkMargins(childCount, layout.Margins)
                    : SkipChildren(childCount));

        private bool WalkMargins(long count, List<ProjectFileTreeField> into)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    // `type` says whether the four margins are identical or mirrored; the numbers are the margins.
                    if (NameAt(key) is "outer" or "inner" or "top" or "bottom" or "left" or "right" or "type")
                    {
                        Keep(into, NameAt(key), Value(value));
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "margins"
                    ? WalkMargins(childCount, into)
                    : SkipChildren(childCount));

        /// <summary>
        /// The casting off: how the bars are made into systems and the systems into frames. A whole node of options,
        /// kept as the file writes them.
        /// </summary>
        private bool WalkCastingOff(long count, LayoutDraft layout)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    if (layout.CastingOff.Count < 12)
                    {
                        Keep(layout.CastingOff, NameAt(key), Value(value));
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        /// <summary>
        /// A layout's page-sequence overrides: the frames it moves or adds. The chain definitions themselves are
        /// counted, not read: the geometry of a frame lives in the page template it belongs to.
        /// </summary>
        private bool WalkPageSequenceOverrides(long count, LayoutDraft layout)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "frameChainDefinitions" => WalkFrameChainDefinitions(childCount, layout),
                    _ => SkipChildren(childCount),
                });

        private bool WalkFrameChainDefinitions(long count, LayoutDraft layout)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "musicFrameChains" => WalkChainList(childCount, layout, music: true),
                    "textFrameChains" => WalkChainList(childCount, layout, music: false),
                    _ => SkipChildren(childCount),
                });

        private bool WalkChainList(long count, LayoutDraft layout, bool music)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) =>
                {
                    if (NameAt(key) != "musicFrameChains" && NameAt(key) != "textFrameChains")
                    {
                        return SkipChildren(childCount);
                    }

                    if (music)
                    {
                        layout.FrameChainOverrides++;
                    }

                    return SkipChildren(childCount);
                });

        /// <summary>The score library: its playing techniques, their appearances, and the page templates.</summary>
        private bool WalkLibrary(long count)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "playingTechniques" => WalkCollection(childCount, "PlayingTechniqueDefinition", WalkTechniqueDefinition),
                    "playingTechniqueAppearanceCollectionDefinition" => WalkCollection(
                        childCount,
                        "PlayingTechniqueAppearanceDefinition",
                        WalkTechniqueAppearance),
                    "pagePairDefinitionSets" => WalkCollection(
                        childCount,
                        "PagePairDefinitionSetDefinition",
                        WalkPageSet),
                    _ => SkipChildren(childCount),
                });

        /// <summary>Walks <c>a → entities → objects</c> and calls the handler for each object of one kind.</summary>
        private bool WalkCollection(long count, string kind, Func<long, bool> onItem)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "entities"
                    ? WalkChildren(
                        childCount,
                        null,
                        (itemKey, itemCount, _) => NameAt(itemKey) == kind
                            ? onItem(itemCount)
                            : SkipChildren(itemCount))
                    : SkipChildren(childCount));

        private bool WalkTechniqueDefinition(long count)
        {
            var definition = new TechniqueDefinitionDraft();

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "entityID": definition.EntityId = Value(value); break;
                        case "name": definition.Name = Value(value); break;
                        case "articulationType": definition.ArticulationType = Value(value); break;
                        case "groupType": definition.GroupType = Value(value); break;
                        case "aliasForPlayingTechniqueID": definition.AliasFor = Value(value); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

            if (ok && definition.EntityId is not null)
            {
                _techniqueDefinitions.Add(definition);
            }

            return ok;
        }

        private bool WalkTechniqueAppearance(long count)
        {
            var appearance = new TechniqueAppearanceDraft();

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "entityID": appearance.EntityId = Value(value); break;
                        case "name": appearance.Name = Value(value); break;
                        case "text": appearance.Text = Value(value); break;
                        case "playingTechniqueEntityID": appearance.TechniqueId = Value(value); break;
                        case "playingTechniqueAppearanceType": appearance.Type = Value(value); break;
                        case "playingTechniqueAppearanceCategory": appearance.Category = Value(value); break;
                        case "language": appearance.Language = Value(value); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

            if (ok && appearance.EntityId is not null)
            {
                _techniqueAppearances.Add(appearance);
            }

            return ok;
        }

        /// <summary>
        /// One page template: the frames of the first page and of a normal page, left and right, and the frame chains
        /// (a music chain carries the music, a text chain carries a heading or a page number).
        /// </summary>
        private bool WalkPageSet(long count)
        {
            var pageSet = new PageSetDraft();

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "entityID": pageSet.EntityId = Value(value); break;
                        case "name": pageSet.Name = Value(value); break;
                        case "type": pageSet.Type = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) switch
                {
                    "frameChainDefinitions" => WalkPageSetChains(childCount, pageSet),
                    "firstPageDefinition" => WalkPageDefinition(childCount, pageSet, "first"),
                    "normalPageDefinition" => WalkPageDefinition(childCount, pageSet, "normal"),
                    _ => SkipChildren(childCount),
                });

            if (ok)
            {
                _pageSets.Add(pageSet);
            }

            return ok;
        }

        private bool WalkPageSetChains(long count, PageSetDraft pageSet)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "musicFrameChains" => WalkChainItems(childCount, pageSet.MusicChains),
                    "textFrameChains" => WalkChainItems(childCount, pageSet.TextChains),
                    _ => SkipChildren(childCount),
                });

        /// <summary>
        /// A chain list is written twice over: an outer node named after the list, then one entry per chain. The outer
        /// node and the entries share their name, so a name test alone would land on the wrong level.
        /// </summary>
        private bool WalkChainItems(long count, List<long> into)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) is "musicFrameChains" or "textFrameChains"
                    ? WalkChildren(
                        childCount,
                        (leafKey, leafValue) =>
                        {
                            if (NameAt(leafKey) == "frameChainDefinitionID")
                            {
                                into.Add(Number(leafValue));
                            }

                            return true;
                        },
                        (nodeKey, nodeCount, _) => NameAt(nodeKey) is "musicFrameChains" or "textFrameChains"
                            ? WalkChainItems(nodeCount, into)
                            : SkipChildren(nodeCount))
                    : SkipChildren(childCount));

        private bool WalkPageDefinition(long count, PageSetDraft pageSet, string kind)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "left" => WalkPageSide(childCount, pageSet, kind, "left"),
                    "right" => WalkPageSide(childCount, pageSet, kind, "right"),
                    _ => SkipChildren(childCount),
                });

        private bool WalkPageSide(long count, PageSetDraft pageSet, string kind, string side)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "frames"
                    ? WalkPageFrames(childCount, pageSet, kind, side)
                    : SkipChildren(childCount));

        private bool WalkPageFrames(long count, PageSetDraft pageSet, string kind, string side)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "frame"
                    ? WalkFrame(childCount, pageSet, kind, side)
                    : SkipChildren(childCount));

        private bool WalkFrame(long count, PageSetDraft pageSet, string kind, string side)
        {
            var frame = new FrameDraft { Kind = kind, Side = side };

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "indexInFrameChainOnPage": frame.Index = (int)Number(value); break;
                        case "contentRelative": frame.ContentRelative = Value(value) == "true"; break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) switch
                {
                    "frameChainDefinitionID" => WalkFrameChain(childCount, frame),
                    "constraints" => WalkFrameConstraints(childCount, frame),
                    _ => SkipChildren(childCount),
                });

            if (ok)
            {
                pageSet.Frames.Add(frame);
            }

            return ok;
        }

        private bool WalkFrameChain(long count, FrameDraft frame)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "frameChainDefinitionID": frame.Chain = Number(value); break;
                        case "isLayoutSpecific": frame.LayoutSpecific = Value(value) == "true"; break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        private bool WalkFrameConstraints(long count, FrameDraft frame)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "positionConstraints" => WalkConstraints(childCount, frame.Position),
                    "dimensionConstraints" => WalkConstraints(childCount, frame.Size),
                    _ => SkipChildren(childCount),
                });

        private bool WalkConstraints(long count, List<ProjectFileTreeField> into)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    if (NameAt(key) is "left" or "right" or "top" or "bottom" or "width" or "height")
                    {
                        Keep(into, NameAt(key), Value(value));
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        private static void Keep(List<ProjectFileTreeField> into, string? key, string? value)
        {
            if (into.Count < 16)
            {
                into.Add(new ProjectFileTreeField(key ?? "?", value));
            }
        }

        private bool WalkPlayers(long count)
            => WalkItems(count, (_, children) =>
            {
                var player = new PlayerDraft();

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "playerID": player.Id = Number(value); break;
                            case "displayName": player.Name = Value(value); break;
                            case "baseName": player.Name ??= Value(value); break;
                            case "displayShortName": player.ShortName = Value(value); break;
                            case "baseShortName": player.ShortName ??= Value(value); break;
                            case "isSoloist": player.IsSoloist = Value(value) == "true"; break;
                        }

                        return true;
                    },
                    (key, childCount, _) => NameAt(key) == "instruments"
                        ? WalkInstruments(childCount, player)
                        : SkipChildren(childCount));

                _players.Add(player);

                return ok;
            });

        /// <summary>
        /// The score's player groups, which is where the checklist's group names and abbreviations live.
        /// </summary>
        /// <remarks>
        /// Measured on a 34-player project: each <c>scorePlayerGroup</c> writes <c>name</c> ("Woodwinds"),
        /// <c>shortName</c> ("Ww."), <c>groupID</c>, <c>insertionIndex</c> and a comma-separated <c>playerIDs</c>, and
        /// that project holds exactly the five groups the checklist names — Woodwinds/Ww., Brass/Br.,
        /// Percussion/Perc., Strings/Str. and a Band/Bnd. that is not on the list. The group's own <c>name</c> and
        /// <c>shortName</c> are therefore readable without a command, which matters because
        /// <c>Project.PlayerGroup.Rename?PlayerGroupID=&amp;Name=</c> was measured to be refused outright
        /// (<c>kError kCommandNotAllowed</c>, and no Posting line in Dorico's log).
        /// </remarks>
        private bool WalkPlayerGroups(long count)
            => WalkItems(count, (_, children) =>
            {
                var group = new PlayerGroupDraft();

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "groupID": group.Id = Number(value); break;
                            case "name": group.Name = Value(value); break;
                            case "shortName": group.ShortName = Value(value); break;
                            case "insertionIndex": group.InsertionIndex = (int)Number(value); break;
                            case "playerIDs": group.PlayerIds.AddRange(Players(Value(value))); break;
                        }

                        return true;
                    },
                    (_, childCount, _) => SkipChildren(childCount));

                _playerGroups.Add(group);

                return ok;
            });

        /// <summary>The comma-separated player ids a group writes, as numbers.</summary>
        private static List<long> Players(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            var ids = new List<long>();

            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (long.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        private bool WalkInstruments(long count, PlayerDraft player)
            => WalkItems(count, (_, children) =>
            {
                var instrument = new InstrumentDraft();

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "instrumentID": instrument.Id = Number(value); break;
                            case "entityID": instrument.EntityId = Value(value); break;
                            case "numStaves": instrument.Staves = (int)Number(value); break;
                            case "standardRange": instrument.Range = Value(value); break;
                        }

                        return true;
                    },
                    (key, childCount, _) => NameAt(key) switch
                    {
                        "nameData" => WalkNameData(childCount, instrument),
                        "transposition" => WalkTransposition(childCount, instrument),
                        _ => SkipChildren(childCount),
                    });

                player.Instruments.Add(instrument);

                return ok;
            });

        /// <summary>
        /// The instrument's transposition, which the file states as
        /// <c>intervalForTransposingScores = "steamVariantList:list: {int: 2}, {string: "kIntervalMajorOrPerfect"}"</c>
        /// — a major-or-perfect interval of two (a major second), five (a perfect fifth) and so on. Only its
        /// presence matters here: with it, a note's written pitch and its sounding pitch differ, so the note's MIDI
        /// number says nothing about the accidental its spelling implies.
        /// </summary>
        private bool WalkTransposition(long count, InstrumentDraft instrument)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    if (NameAt(key) == "intervalForTransposingScores")
                    {
                        instrument.Transposing = true;
                        instrument.Transposition = Value(value);
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        private bool WalkNameData(long count, InstrumentDraft instrument)            => WalkChildren(
                count,
                (key, value) =>
                {
                    if (NameAt(key) == "uiName")
                    {
                        instrument.Name = Value(value);
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        // ------------------------------------------------------------ flows

        private bool WalkFlows(long count)
            => WalkItems(count, (_, children) => WalkFlow(children));

        private bool WalkFlow(long count)
        {
            var flow = new FlowDraft();
            var blocksOffset = -1;
            long blocksCount = 0;

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "flowID": flow.Id = Number(value); break;
                        case "flowName": flow.Name = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, start) =>
                {
                    switch (NameAt(key))
                    {
                        case "flowPlayers": return WalkFlowPlayers(childCount);
                        case "blockInstances": return WalkBlockInstances(childCount);
                        case "eventStreams": return WalkEventStreams(childCount, flow);
                        case "elementTables": return WalkElementTables(childCount, flow);

                        // Blocks hold the notes and the bar-division events, and they need the stream map the other
                        // children build. Remember where they start and come back to them below.
                        case "blocks":
                            blocksOffset = start;
                            blocksCount = childCount;

                            return SkipChildren(childCount);
                        default: return SkipChildren(childCount);
                    }
                });

            if (!ok)
            {
                return false;
            }

            if (blocksOffset >= 0)
            {
                // Come back to the end of the flow's children afterwards: the blocks sit in the middle of them, and a
                // walk leaves the cursor wherever the subtree ended.
                var resume = _pos;

                _pos = blocksOffset;

                if (!ReadHeader(out _, out var children))
                {
                    return false;
                }

                var walked = WalkBlocks(children, flow);

                _pos = resume;

                if (!walked)
                {
                    return false;
                }
            }

            _flows.Add(flow);

            return true;
        }

        /// <summary>
        /// The stave and voice streams of every player in the flow: the map that turns a note's stream into a player,
        /// an instrument, a stave and a voice.
        /// </summary>
        private bool WalkFlowPlayers(long count)
            => WalkItems(count, (_, children) =>
            {
                long playerId = -1;
                long playerStream = -1;

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "playerID": playerId = Number(value); break;
                            case "eventStreamID": playerStream = Number(value); break;
                        }

                        return true;
                    },
                    (key, childCount, _) => NameAt(key) == "flowPlayerInstruments"
                        ? WalkFlowPlayerInstruments(childCount, playerId)
                        : SkipChildren(childCount));

                if (playerStream >= 0)
                {
                    _streamStaves.TryAdd(
                        playerStream,
                        new StaveDraft(playerStream, "kPlayerStream", playerId, null, null, null));
                }

                return ok;
            });

        private bool WalkFlowPlayerInstruments(long count, long playerId)
            => WalkItems(count, (_, children) =>
            {
                long instrumentId = -1;

                return WalkChildren(
                    children,
                    (key, value) =>
                    {
                        if (NameAt(key) == "instrumentID")
                        {
                            instrumentId = Number(value);
                        }

                        return true;
                    },
                    (key, childCount, _) => NameAt(key) switch
                    {
                        "staveStreamInfos" => WalkStreamInfos(childCount, playerId, instrumentId, stave: true),
                        "voiceStreamInfos" => WalkStreamInfos(childCount, playerId, instrumentId, stave: false),
                        _ => SkipChildren(childCount),
                    });
            });

        private bool WalkStreamInfos(long count, long playerId, long instrumentId, bool stave)
        {
            var voice = 0;

            return WalkItems(count, (_, children) =>
            {
                long stream = -1;
                var staveIndex = (int?)null;
                var logicalStave = (int?)null;

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "staveStreamID": stream = Number(value); break;
                            case "voiceStreamID": stream = Number(value); break;
                            case "staveID": staveIndex = (int)Number(value); break;
                            case "nthStaveInInstrument": staveIndex ??= (int)Number(value); break;
                            case "defaultLogicalStaveID": logicalStave = (int)Number(value); break;
                        }

                        return true;
                    },
                    (_, childCount, _) => SkipChildren(childCount));

                if (stream < 0)
                {
                    return ok;
                }

                if (stave)
                {
                    _streamStaves[stream] = new StaveDraft(
                        stream,
                        "kInstrumentStaveStream",
                        playerId,
                        instrumentId,
                        staveIndex is null ? null : staveIndex + 1,
                        null);
                }
                else
                {
                    voice++;

                    _streamStaves[stream] = new StaveDraft(
                        stream,
                        "kVoiceStream",
                        playerId,
                        instrumentId,
                        logicalStave is null ? null : logicalStave + 1,
                        voice);
                }

                return ok;
            });
        }

        private bool WalkBlockInstances(long count)
            => WalkItems(count, (_, children) =>
            {
                long blockId = -1;
                long streamId = -1;

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "blockID": blockId = Number(value); break;
                            case "streamID": streamId = Number(value); break;
                        }

                        return true;
                    },
                    (_, childCount, _) => SkipChildren(childCount));

                if (blockId >= 0 && streamId >= 0)
                {
                    _blockStreams[blockId] = streamId;
                }

                return ok;
            });

        private bool WalkEventStreams(long count, FlowDraft flow)
            => WalkItems(count, (_, children) =>
            {
                long streamId = -1;
                string? kind = null;

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "streamID": streamId = Number(value); break;
                            case "eventStreamType": kind = Value(value); break;
                        }

                        return true;
                    },
                    (_, childCount, _) => SkipChildren(childCount));

                if (streamId >= 0)
                {
                    flow.Streams++;

                    if (kind is not null)
                    {
                        _streamKinds[streamId] = kind;

                        if (_streamStaves.TryGetValue(streamId, out var existing))
                        {
                            _streamStaves[streamId] = existing with { Kind = kind };
                        }
                    }
                }

                return ok;
            });

        // ------------------------------------------------------------ element tables

        /// <summary>
        /// A flow's element tables. Three of them are read: the bar divisions (the metre), the dynamic groups (what a
        /// dynamic marking looks like) and the lyrics. Their items live in an <c>array</c> child, and an <b>empty</b>
        /// table writes that <c>array</c> as a leaf instead of a node — which is why a table is read through
        /// <see cref="WalkElementItems"/> and not through <see cref="WalkItems"/>.
        /// </summary>
        private bool WalkElementTables(long count, FlowDraft flow)
            => WalkItems(count, (kind, children) =>
            {
                switch (NameAt(kind))
                {
                    case "BarDivisionElementTableDefinition":
                        return WalkElementItems(children, WalkBarDivisionElement, out _);

                    case "DynamicGroupElementTableDefinition":
                        return WalkElementItems(children, element => WalkDynamicGroupElement(element, flow), out _);

                    case "LyricElementTableDefinition":
                        var ok = WalkElementItems(children, element => WalkLyricElement(element, flow), out var elements);
                        flow.LyricTables++;
                        flow.LyricElements += elements;

                        return ok;

                    case "SlurElementTableDefinition":
                        return WalkElementItems(children, element => WalkSlurElement(element, flow), out _);

                    default:
                        return SkipChildren(children);
                }
            });

        /// <summary>
        /// The items of one element table, with how many there were. Dorico writes the collection as <c>array</c>: a
        /// node holding the items, or — when the table has nothing in it — a leaf, which is exactly what every corpus
        /// file's lyric table is.
        /// </summary>
        private bool WalkElementItems(long count, Func<long, bool> onItem, out int items)
        {
            items = 0;

            if (count == 1 && _pos < _data.Length && IsLeafTag(_data[_pos]))
            {
                var save = _pos;

                if (!ReadLeaf(out var key, out _) || NameAt(key) != "array")
                {
                    _pos = save;
                }
                else
                {
                    return true;
                }
            }

            var seen = 0;

            var ok = WalkItems(count, (_, children) =>
            {
                seen++;

                return onItem(children);
            });

            items = seen;

            return ok;
        }

        private bool WalkBarDivisionElement(long count)
        {
            long elementId = -1;

            return WalkChildren(
                count,
                (key, value) =>
                {
                    if (NameAt(key) == "elementID")
                    {
                        elementId = Number(value);
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "barDivisionData"
                    ? WalkBarDivisionData(childCount, elementId)
                    : SkipChildren(childCount));
        }

        /// <summary>
        /// One dynamic group definition: the markings that make it up. The group itself has no position — the events
        /// that name it carry that.
        /// </summary>
        private bool WalkDynamicGroupElement(long count, FlowDraft flow)
        {
            var group = new DynamicGroupDraft();

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "elementID": group.Id = Number(value); break;
                        case "referencingEventIDs": group.Events = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "subElements"
                    ? WalkDynamicSubElements(childCount, group)
                    : SkipChildren(childCount));

            if (ok && group.Id >= 0)
            {
                flow.Groups[group.Id] = group;
            }

            return ok;
        }

        private bool WalkDynamicSubElements(long count, DynamicGroupDraft group)
            => WalkItems(count, (kind, children) =>
            {
                var mark = new DynamicMarkDraft(NameAt(kind) ?? "unknown");

                var ok = WalkChildren(
                    children,
                    (key, value) =>
                    {
                        switch (NameAt(key))
                        {
                            case "position": mark.Position = Value(value); break;
                            case "duration": mark.Duration = Value(value); break;
                            case "intensity": mark.Intensity = Value(value); break;
                            case "intensityLevel": mark.IntensityLevel = (int)Number(value); break;

                            // Only a gradual change has a hairpin direction; another kind that carries a field of the
                            // same name means something else by it, so it stays raw.
                            case "direction" when mark.Kind == "GradualChangeDynamicDefinition":
                                mark.Direction = Value(value);
                                break;

                            // A field this reader has not seen (another kind of dynamic, a version that adds
                            // fields): keep it rather than drop it, so the report can still show what the file says.
                            default: mark.Keep(NameAt(key), Value(value)); break;
                        }

                        return true;
                    },
                    (key, childCount, _) => NameAt(key) == "commonDef"
                        ? WalkDynamicCommonDef(childCount, mark)
                        : SkipChildren(childCount));

                group.Marks.Add(mark);

                return ok;
            });

        private bool WalkDynamicCommonDef(long count, DynamicMarkDraft mark)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    if (NameAt(key) == "subito")
                    {
                        mark.Subito = Value(value);
                    }
                    else
                    {
                        mark.Keep(NameAt(key), Value(value));
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        /// <summary>
        /// One lyric of a lyric element table.
        /// <para>
        /// No file in the corpus has one, so the shape is not known from data — but the application's own vocabulary
        /// is: its IO type is <c>LyricElementDefinition</c>, its event is <c>LyricEventDefinition</c>, the event
        /// points at an element with <c>lyricElementID</c>, and a lyric's line is addressed by
        /// <c>lyricLineNumberAndStaveIDAndLocation</c>. Everything below is read <i>by name</i> on that basis, keeps
        /// the raw leaves as they are, and records which key it took each reading from, so a real lyric element
        /// cannot be silently misread: either the expected key is there and is used, or the raw
        /// <see cref="ProjectFileLyricElement.Fields"/> say exactly what the file holds.
        /// </para>
        /// </summary>
        private bool WalkLyricElement(long count, FlowDraft flow)
        {
            var element = new LyricElementDraft();
            var fields = new List<string>();

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    var name = NameAt(key);

                    Note(fields, name);
                    element.Keep(name, Value(value));

                    // The element's own id. lyricElementID is the name the application uses for a lyric; elementID is
                    // what the other element tables of this format write, kept as a fallback for a file that differs.
                    if (element.Id < 0 && name is "lyricElementID" or "elementID")
                    {
                        var id = Number(value);

                        if (id >= 0)
                        {
                            element.Id = id;
                            element.IdKey = name;
                        }
                    }

                    return true;
                },
                (key, children, _) =>
                {
                    var name = NameAt(key);

                    Note(fields, name);
                    element.KeepChild(name);

                    return name == "propertyTable"
                        ? WalkPropertyTable(children, element.Properties)
                        : SkipChildren(children);
                });

            if (ok)
            {
                flow.LyricShapes.Add(fields);
                flow.LyricReadings.Add(element);
            }

            return ok;
        }

        /// <summary>
        /// One lyric placed in the music, by the same shape as a dynamic: the event carries where it sits, and the
        /// element it names (through <c>lyricElementID</c>, by analogy with <c>slurElementID</c> and
        /// <c>dynamicGroupID</c>) carries the text. The event's own property table is read as properties, because the
        /// property tags the application knows for a lyric (<c>kLyricLineNumber</c>, <c>kLyricLineLocation</c>,
        /// <c>kLyricSyllableType</c>, <c>kLyricIsChorus</c>, <c>kLyricIsTranslation</c>, ...) are what could say
        /// which line and which verse a syllable belongs to.
        /// </summary>
        private bool WalkLyricEvent(long count, long stream, FlowDraft flow)
        {
            var lyric = new LyricEventDraft { Stream = stream, Flow = flow };

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "eventID": lyric.EventId = Number(value); break;
                        case "position": lyric.Position = ScoreRational.Parse(Value(value)); break;
                        case "duration": lyric.Duration = Value(value) ?? string.Empty; break;
                        case "lyricElementID": lyric.Element = Number(value); break;

                        // A lyric's text may sit on the event rather than on the element: keep whatever else the
                        // event carries so the report can show it, whichever side the file puts it on.
                        default: lyric.Keep(NameAt(key), Value(value)); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "propertyTable"
                    ? WalkPropertyTable(childCount, lyric.Properties)
                    : SkipChildren(childCount));

            if (ok)
            {
                flow.LyricEventCount++;
                _lyricEvents.Add(lyric);
            }

            return ok;
        }

        /// <summary>
        /// One slur element: the shared definition a slur's events point at. Its own content is the list of the events
        /// that reference it — a slur over a chord is one element referenced by one event per notehead.
        /// </summary>
        private bool WalkSlurElement(long count, FlowDraft flow)
        {
            var element = new SlurElementDraft();

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "elementID": element.Id = Number(value); break;
                        case "referencingEventIDs": element.Events = Value(value); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

            if (ok && element.Id >= 0)
            {
                flow.SlurElements[element.Id] = element;
            }

            return ok;
        }

        /// <summary>
        /// The field names of a lyric, and nothing else: what a lyric holds is not modelled here (no file in the
        /// corpus has one), so the honest report is the shape and not a reading of it.
        /// </summary>
        private static void Note(List<string> fields, string? name)
        {
            if (name is not null && !fields.Contains(name))
            {
                fields.Add(name);
            }
        }

        private bool WalkBarDivisionData(long count, long elementId)
        {
            var signature = new TimeSignatureDraft();

            var ok = WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "timeSignature"
                    ? WalkTimeSignature(childCount, signature)
                    : SkipChildren(childCount));

            if (ok && elementId >= 0 && signature.Numerator > 0 && signature.Denominator > 0)
            {
                _barElements[elementId] = new ElementDraft(signature.Numerator, signature.Denominator);
            }

            return ok;
        }

        private bool WalkTimeSignature(long count, TimeSignatureDraft signature)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "timeSignaturesAndDivisions"
                    ? WalkTimeSignatureAndDivisions(childCount, signature)
                    : SkipChildren(childCount));

        private bool WalkTimeSignatureAndDivisions(long count, TimeSignatureDraft signature)
            => WalkItems(count, (_, division) =>
            {
                var local = new TimeSignatureDraft();

                var ok = WalkChildren(
                    division,
                    null,
                    (key, childCount, _) => NameAt(key) == "timeSignature"
                        ? WalkTimeSignatureNumbers(childCount, local)
                        : SkipChildren(childCount));

                if (local.Numerator > 0 && local.Denominator > 0)
                {
                    signature.Numerator = local.Numerator;
                    signature.Denominator = local.Denominator;
                }

                return ok;
            });

        private bool WalkTimeSignatureNumbers(long count, TimeSignatureDraft signature)
            => WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "numerator": signature.Numerator = (int)Number(value); break;
                        case "denominator": signature.Denominator = (int)Number(value); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

        // ------------------------------------------------------------ blocks, events, notes

        private bool WalkBlocks(long count, FlowDraft flow)
            => WalkItems(count, (_, children) =>
            {
                flow.Blocks++;

                return WalkBlock(children, flow);
            });

        private bool WalkBlock(long count, FlowDraft flow)
        {
            long blockId = -1;
            string? streamKind = null;
            var eventsOffset = -1;
            long eventsCount = 0;

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "blockID": blockId = Number(value); break;
                        case "parentEventStreamType": streamKind = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, start) =>
                {
                    if (NameAt(key) != "events")
                    {
                        return SkipChildren(childCount);
                    }

                    eventsOffset = start;
                    eventsCount = childCount;

                    return SkipChildren(childCount);
                });

            if (!ok || eventsOffset < 0)
            {
                return ok;
            }

            var stream = _blockStreams.GetValueOrDefault(blockId, -1);

            if (stream >= 0 && streamKind is not null)
            {
                _streamKinds[stream] = streamKind;

                if (_streamStaves.TryGetValue(stream, out var existing))
                {
                    _streamStaves[stream] = existing with { Kind = streamKind };
                }
            }

            _pos = eventsOffset;

            if (!ReadHeader(out _, out var events) || events != eventsCount)
            {
                return false;
            }

            return WalkEvents(events, stream, flow);
        }

        private bool WalkEvents(long count, long stream, FlowDraft flow)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) switch
                {
                    "NoteEventDefinition" => WalkNote(childCount, stream, flow),
                    "SlurEventDefinition" => WalkSlurEvent(childCount, stream, flow),
                    "PlayingTechniqueEventDefinition" => WalkTechniqueEvent(childCount, stream, flow),
                    "BarDivisionEventDefinition" => WalkBarDivisionEvent(childCount),
                    "DynamicGroupEventDefinition" => WalkDynamicEvent(childCount, stream, flow),
                    "LyricEventDefinition" => WalkLyricEvent(childCount, stream, flow),
                    _ => SkipChildren(childCount),
                });

        /// <summary>
        /// One slur, placed in the music. The event carries where it starts and how long it is; the element it names
        /// carries nothing but the list of the events that share it, so two events of the same element are two
        /// noteheads of one slur.
        /// </summary>
        private bool WalkSlurEvent(long count, long stream, FlowDraft flow)
        {
            var slur = new SlurDraft { Stream = stream, Flow = flow };

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "eventID": slur.EventId = Number(value); break;
                        case "position": slur.Position = ScoreRational.Parse(Value(value)); break;
                        case "duration": slur.Duration = Value(value) ?? string.Empty; break;
                        case "slurElementID": slur.Element = Number(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "propertyTable"
                    ? WalkPropertyTable(childCount, slur.Properties)
                    : SkipChildren(childCount));

            if (ok)
            {
                flow.Slurs++;
                _slurs.Add(slur);
            }

            return ok;
        }

        /// <summary>
        /// A playing technique placed in the music as its own event. The file writes which <i>appearance</i> is shown
        /// (<c>pta.pizzText</c>); the appearance's definition names the technique (<c>pt.pizzicato</c>) behind it.
        /// </summary>
        private bool WalkTechniqueEvent(long count, long stream, FlowDraft flow)
        {
            var technique = new TechniqueEventDraft { Stream = stream };

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "eventID": technique.EventId = Number(value); break;
                        case "position": technique.Position = ScoreRational.Parse(Value(value)); break;
                        case "duration": technique.Duration = Value(value) ?? string.Empty; break;
                        case "appearanceEntityID": technique.AppearanceId = Value(value); break;
                        case "appearanceType": technique.AppearanceType = Value(value); break;
                        case "data": technique.Data = Value(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "propertyTable"
                    ? WalkPropertyTable(childCount, technique.Properties)
                    : SkipChildren(childCount));

            if (ok)
            {
                flow.Techniques++;
                _techniqueEvents.Add(technique);
            }

            return ok;
        }

        /// <summary>
        /// A note's property table: the decorations that are not events of their own — articulations, a tie's
        /// engraving, a beam break, a playing technique attached to the note. An empty table is written as a leaf
        /// named <c>entries</c> with no value, so it is read through the leaf as well as the node.
        /// </summary>
        private bool WalkPropertyTable(long count, List<PropertyDraft> into)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "entries"
                    ? WalkPropertyEntries(childCount, into)
                    : SkipChildren(childCount));

        private bool WalkPropertyEntries(long count, List<PropertyDraft> into)
            => WalkChildren(
                count,
                null,
                (key, childCount, _) => NameAt(key) == "entry"
                    ? WalkPropertyEntry(childCount, into)
                    : SkipChildren(childCount));

        private bool WalkPropertyEntry(long count, List<PropertyDraft> into)
        {
            string? type = null;
            string? value = null;

            var ok = WalkChildren(
                count,
                (key, leafValue) =>
                {
                    switch (NameAt(key))
                    {
                        case "propertyType": type = Value(leafValue); break;
                        case "value": value = Value(leafValue); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

            if (ok && type is not null)
            {
                into.Add(new PropertyDraft(type, value));
            }

            return ok;
        }

        /// <summary>
        /// A dynamic marking placed in the music. The event says where it goes and which group it is; the group
        /// definition in the flow's element tables says what it looks like.
        /// </summary>
        private bool WalkDynamicEvent(long count, long stream, FlowDraft flow)
        {
            var mark = new DynamicDraft { Stream = stream, Flow = flow };

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "eventID": mark.EventId = Number(value); break;
                        case "position": mark.Position = ScoreRational.Parse(Value(value)); break;
                        case "duration": mark.Duration = Value(value) ?? string.Empty; break;
                        case "dynamicGroupID": mark.Group = Number(value); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

            if (ok && mark.Group >= 0)
            {
                flow.Dynamics++;
                _dynamicEvents.Add(mark);
            }

            return ok;
        }

        private bool WalkNote(long count, long stream, FlowDraft flow)
        {
            var note = new NoteDraft { Stream = stream };

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "eventID": note.EventId = Number(value); break;
                        case "position": note.Position = Value(value) is { } position ? ScoreRational.Parse(position) : ScoreRational.Zero; break;
                        case "duration": note.Duration = Value(value) ?? string.Empty; break;
                        case "pitch": note.Pitch = Value(value); break;
                        case "logicalStaveIDOverride": note.Stave = (int)Number(value); break;
                    }

                    return true;
                },
                (key, childCount, _) => NameAt(key) == "propertyTable"
                    ? WalkPropertyTable(childCount, note.Properties)
                    : SkipChildren(childCount));

            if (ok)
            {
                flow.Notes++;
                _notes.Add(note);
            }

            return ok;
        }

        private bool WalkBarDivisionEvent(long count)
        {
            long elementId = -1;
            var position = ScoreRational.Zero;

            var ok = WalkChildren(
                count,
                (key, value) =>
                {
                    switch (NameAt(key))
                    {
                        case "barDivisionElementID": elementId = Number(value); break;
                        case "position": position = ScoreRational.Parse(Value(value)); break;
                    }

                    return true;
                },
                (_, childCount, _) => SkipChildren(childCount));

            if (elementId >= 0)
            {
                _barEvents.Add(new MeterEventDraft(elementId, position));
            }

            return ok;
        }

        // ------------------------------------------------------------ output

    }
}