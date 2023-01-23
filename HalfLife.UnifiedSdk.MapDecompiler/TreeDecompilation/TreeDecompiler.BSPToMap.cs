﻿using Sledge.Formats.Bsp.Objects;
using Sledge.Formats.Id;
using Sledge.Formats.Map.Objects;
using MapFace = Sledge.Formats.Map.Objects.Face;

namespace HalfLife.UnifiedSdk.MapDecompiler.TreeDecompilation
{
    internal partial class TreeDecompiler
    {
        private void BSPBrushToMapBrush(BspBrush brush, DecompiledEntity entity, Vector3 origin)
        {
            var besttexinfo = TexInfoNode;

            foreach (var side in brush.Sides)
            {
                if (side.Winding is null)
                {
                    continue;
                }

                if (side.TextureInfo != TexInfoNode)
                {
                    //this brush side is textured
                    besttexinfo = side.TextureInfo;
                }
            }

            if (besttexinfo == TexInfoNode)
            {
                // Allow textureless brushes to exist if the entity is a trigger or a func_nogrenades
                if (!entity.Entity.ClassName.StartsWith("trigger_") && !entity.Entity.ClassName.StartsWith("func_nogrenades")) {
                    // TODO: maybe add a clip texture to it and keep the brush
                    brush.Sides.Clear();
                    ++_numClipBrushes;
                    return;
                }
            }

            //set the texinfo for all the brush sides without texture
            foreach (var side in brush.Sides)
            {
                if (side.TextureInfo == TexInfoNode)
                {
                    side.TextureInfo = _options.ApplyNullToGeneratedFaces ? _nullTextureInfo : besttexinfo;
                }
            }

            var decompiledBrush = new DecompiledBrush(_numMapBrushes, brush);

            //create windings for sides and bounds for brush
            if (!MakeBrushWindings(entity, decompiledBrush))
            {
                return;
            }

            //add brush bevels
            AddBrushBevels(decompiledBrush);
            //a new brush has been created
            ++_numMapBrushes;

            if (!_options.IncludeLiquids)
            {
                if (brush.Side == Contents.Water
                    || brush.Side == Contents.Slime
                    || brush.Side == Contents.Lava)
                {
                    return;
                }
            }

            var mapbrush = BSPBrushToMap(decompiledBrush.Brush, origin);

            // If this mapbrush was linked to a trigger in goldsrc,
            // make sure to give it the trigger texture!!
            if (entity.Entity.ClassName.StartsWith("trigger_") || entity.Entity.ClassName.StartsWith("func_nogrenades"))
            {
                foreach (var face in mapbrush.Faces)
                {
                    face.TextureName = "tools/toolstrigger";
                }
            }

             entity.Entity.Children.Add(mapbrush);
        }

        /// <summary>
        /// creates windings for sides and mins / maxs for the brush
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="ob"></param>
        private bool MakeBrushWindings(DecompiledEntity entity, DecompiledBrush ob)
        {
            MathUtils.ClearBounds(ref ob.Mins, ref ob.Maxs);

            foreach (var side1 in ob.Brush.Sides)
            {
                var plane = _bspPlanes[side1.PlaneNumber];

                var w = Winding.BaseWindingForPlane(plane.Normal, plane.Distance);

                foreach (var side2 in ob.Brush.Sides)
                {
                    if (side1 == side2)
                    {
                        continue;
                    }

                    if ((side2.Flags & SideFlag.Bevel) != 0)
                    {
                        continue;
                    }

                    var plane2 = _bspPlanes[side2.PlaneNumber ^ 1];
                    w = w.ChopWindingInPlace(plane2.Normal, plane2.Distance, 0); //CLIP_EPSILON);

                    if (w is null)
                    {
                        break;
                    }
                }

                side1.Winding = w;
                if (w is not null)
                {
                    side1.Flags |= SideFlag.Visible;
                    foreach (var point in w.Points)
                    {
                        MathUtils.AddPointToBounds(point, ref ob.Mins, ref ob.Maxs);
                    }
                }
            }

            bool CheckBounds(double min, double max)
            {
                if (min < -Winding.BogusRange || max > Winding.BogusRange)
                {
                    _logger.Information("entity {EntityIndex}, brush {BrushIndex}: bounds out of range", entity.Index, ob.Index);
                    return false;
                }

                if (min > Winding.BogusRange || max < -Winding.BogusRange)
                {
                    _logger.Information("entity {EntityIndex}, brush {BrushIndex}: no visible sides on brush", entity.Index, ob.Index);
                    return false;
                }

                return true;
            }

            if (!CheckBounds(ob.Mins.X, ob.Maxs.X)
                || !CheckBounds(ob.Mins.Y, ob.Maxs.Y)
                || !CheckBounds(ob.Mins.Z, ob.Maxs.Z))
            {
                //remove the brush
                return false;
            }

            return true;
        }

        /// <summary>
        /// Adds any additional planes necessary to allow the brush to be expanded against axial bounding boxes
        /// </summary>
        /// <param name="b"></param>
        /// <exception cref="InvalidOperationException"></exception>
        private void AddBrushBevels(DecompiledBrush b)
        {
            //
            // add the axial planes
            //
            int order = 0;
            for (int axis = 0; axis < 3; ++axis)
            {
                for (int dir = -1; dir <= 1; dir += 2, ++order)
                {
                    // see if the plane is allready present
                    int i = b.Brush.Sides.FindIndex(s => Vector3Utils.GetByIndex(_bspPlanes[s.PlaneNumber].Normal, axis) == dir);

                    if (i == -1)
                    {
                        i = b.Brush.Sides.Count;
                    }

                    if (i == b.Brush.Sides.Count)
                    {   // add a new side
                        var normal = Vector3.Zero;
                        Vector3Utils.SetByIndex(ref normal, axis, dir);

                        var dist = dir == 1 ? Vector3Utils.GetByIndex(ref b.Maxs, axis) : -Vector3Utils.GetByIndex(ref b.Mins, axis);

                        var firstSide = b.Brush.Sides[0];

                        var side = new BspSide
                        {
                            PlaneNumber = FindFloatPlane(normal, dist),
                            TextureInfo = firstSide.TextureInfo,
                            Contents = firstSide.Contents,
                            Flags = SideFlag.Bevel
                        };

                        b.Brush.Sides.Add(side);
                    }

                    // if the plane is not in it canonical order, swap it
                    if (i != order)
                    {
                        (b.Brush.Sides[i], b.Brush.Sides[order]) = (b.Brush.Sides[order], b.Brush.Sides[i]);
                    }
                }
            }

            //
            // add the edge bevels
            //
            if (b.Brush.Sides.Count == 6)
                return;     // pure axial

            // test the non-axial plane edges
            for (int i = 6; i < b.Brush.Sides.Count; ++i)
            {
                var s = b.Brush.Sides[i];
                var w = s.Winding;

                if (w is null)
                {
                    continue;
                }

                for (int j = 0; j < w.Points.Count; ++j)
                {
                    var k = (j + 1) % w.Points.Count;

                    var vec = w.Points[j] - w.Points[k];

                    if (vec.Length < 0.5)
                    {
                        continue;
                    }

                    vec = Vector3D.Normalize(vec);

                    MathUtils.SnapVector(ref vec);

                    {
                        int vi;
                        for (vi = 0; vi < 3; ++vi)
                        {
                            if (Vector3Utils.GetByIndex(ref vec, vi) == -1 || Vector3Utils.GetByIndex(ref vec, vi) == 1)
                            {
                                break;  // axial
                            }
                        }

                        if (vi != 3)
                        {
                            continue;   // only test non-axial edges
                        }
                    }

                    // try the six possible slanted axials from this edge
                    for (int axis = 0; axis < 3; ++axis)
                    {
                        for (int dir = -1; dir <= 1; dir += 2)
                        {
                            // construct a plane
                            var vec2 = Vector3.Zero;
                            Vector3Utils.SetByIndex(ref vec2, axis, dir);
                            var normal = Vector3D.Cross(vec, vec2);

                            if (normal.Length < 0.5)
                                continue;

                            normal = Vector3D.Normalize(normal);

                            var dist = Vector3D.Dot(w.Points[j], normal);

                            // if all the points on all the sides are
                            // behind this plane, it is a proper edge bevel
                            if (b.Brush.Sides.Find(s =>
                            {
                                // if this plane has allready been used, skip it
                                if (MathUtils.PlaneEqual(_bspPlanes[s.PlaneNumber], normal, dist))
                                {
                                    return true;
                                }

                                var w2 = s.Winding;

                                if (w2 is null)
                                    return false;

                                return w2.Points.FindIndex(p =>
                                {
                                    var d = Vector3D.Dot(p, normal) - dist;
                                    if (d > 0.1)
                                        return true;  // point in front

                                    return false;
                                }) != -1;
                            }) != null)
                            {
                                continue;   // wasn't part of the outer hull
                            }

                            // add this plane
                            var firstSide = b.Brush.Sides[0];

                            var s2 = new BspSide
                            {
                                PlaneNumber = FindFloatPlane(normal, dist),
                                TextureInfo = firstSide.TextureInfo,
                                Contents = firstSide.Contents,
                                Flags = SideFlag.Bevel
                            };

                            b.Brush.Sides.Add(s2);
                        }
                    }
                }
            }
        }

        private static readonly TextureInfo ClipTextureInfo = new()
        {
            TextureName = "CLIP",
            MipTexture = -1
        };

        private static readonly MipTexture ClipTexture = new()
        {
            Name = "CLIP"
        };

        private Solid BSPBrushToMap(BspBrush bspBrush, Vector3 origin)
        {
            var mapBrush = new Solid();

            foreach (var side in bspBrush.Sides)
            {
                //don't write out bevels
                if (side.Flags.HasFlag(SideFlag.Bevel))
                {
                    continue;
                }

                var textureInfo = side.TextureInfo != TexInfoNode ? _bspTexInfo[side.TextureInfo] : ClipTextureInfo;
                var texture = textureInfo.MipTexture != -1 ? _bspTextures[textureInfo.MipTexture] : ClipTexture;

                var s = textureInfo.S.ToDouble();
                var t = textureInfo.T.ToDouble();

                //make sure the two vectors aren't of zero length otherwise use the default
                //vector to prevent a divide by zero in the map writing
                if (new Vector3(s.X, s.Y, s.Z).Length < 0.01)
                {
                    s = Vector4.UnitX;
                }

                if (new Vector3(t.X, t.Y, t.Z).Length < 0.01)
                {
                    t = Vector4.UnitX;
                }

                int planeNumber;

                //if the entity has an origin set
                if (origin != Vector3.Zero)
                {
                    var originalPlane = _bspPlanes[side.PlaneNumber];

                    var newdist = originalPlane.Distance + Vector3D.Dot(originalPlane.Normal, origin);
                    planeNumber = FindFloatPlane(originalPlane.Normal, newdist);
                }
                else
                {
                    planeNumber = side.PlaneNumber;
                }

                //always take the first plane, then flip the points if necesary
                var plane = _bspPlanes[planeNumber & ~1];

                var textureProperties = TextureUtils.CalculateTextureProperties(s, t, origin, plane.Normal);

                var face = new MapFace
                {
                    TextureName = texture.Name,
                    //the scaling of the texture
                    XScale = textureProperties.XScale,
                    YScale = textureProperties.YScale,
                    XShift = textureProperties.XShift,
                    YShift = textureProperties.YShift,
                    Rotation = textureProperties.Rotation,
                    UAxis = textureProperties.UAxis,
                    VAxis = textureProperties.VAxis
                };

                var w = Winding.BaseWindingForPlane(plane.Normal, plane.Distance);

                for (int i = 0; i < 3; ++i)
                {
                    for (int j = 0; j < 3; ++j)
                    {
                        var point = w.Points[i];

                        var value = Vector3Utils.GetByIndex(ref point, j);

                        if (Math.Abs(value) < 0.2) Vector3Utils.SetByIndex(ref point, j, 0);
                        else if (Math.Abs((int)value - value) < 0.3) Vector3Utils.SetByIndex(ref point, j, (int)value);

                        w.Points[i] = point;
                    }
                }

                //three non-colinear points to define the plane
                int p1 = (planeNumber & 1) switch
                {
                    0 => 0,
                    _ => 1
                };

                face.Vertices.Add(w.Points[p1].ToSingle());
                face.Vertices.Add(w.Points[1 - p1].ToSingle());
                face.Vertices.Add(w.Points[2].ToSingle());

                mapBrush.Faces.Add(face);
            }

            return mapBrush;
        }

        private void AddOriginBrush(DecompiledEntity entity, Vector3 origin)
        {
            var min = origin + (Vector3.One * -MapDecompilerConstants.OriginBrushHalfSize);
            var max = origin + (Vector3.One * MapDecompilerConstants.OriginBrushHalfSize);

            var brush = BrushFromBounds(min, max);

            brush.Side = Contents.Origin;

            // Set ORIGIN texture.
            foreach (var side in brush.Sides)
            {
                var plane = _bspPlanes[side.PlaneNumber];

                TextureUtils.TextureAxisFromPlane(plane.Normal, out var uAxis, out var vAxis);

                var texInfo = new TextureInfo
                {
                    MipTexture = _originTextureIndex,
                    S = new(uAxis.ToSingle(), 0),
                    T = new(vAxis.ToSingle(), 0)
                };

                side.TextureInfo = _bspTexInfo.Count;

                _bspTexInfo.Add(texInfo);
            }

            // Convert to map brush.
            entity.Entity.Children.Add(BSPBrushToMap(brush, Vector3.Zero));
            ++_numMapBrushes;
        }
    }
}
