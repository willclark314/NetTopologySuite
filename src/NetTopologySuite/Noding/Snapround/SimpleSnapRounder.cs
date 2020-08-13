using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace NetTopologySuite.Noding.Snapround
{
    /// <summary>
    /// Uses Snap Rounding to compute a rounded,
    /// fully noded arrangement from a set of <see cref="ISegmentString" />s.
    /// <para/>
    /// Implements the Snap Rounding technique described in
    /// the papers by Hobby, Guibas &amp; Marimont, and Goodrich et al.
    /// Snap Rounding enforces that all vertices lie on a uniform grid,
    /// which is determined by the provided <seealso cref="PrecisionModel"/>.
    /// Input vertices do not have to be rounded to this grid;
    /// this will be done during the snap-rounding process.
    /// <para/>
    /// This implementation uses simple iteration over the line segments.
    /// This is not an efficient approach for large sets of segments.
    /// This implementation appears to be fully robust using an integer precision model.
    /// It will function with non-integer precision models, but the
    /// results are not 100% guaranteed to be correctly noded.
    /// 
    /// </summary>
    [Obsolete]
    public class SimpleSnapRounder : INoder
    {
        private readonly PrecisionModel _pm;
        private readonly double _scaleFactor;
        private readonly IDictionary<Coordinate, HotPixel> _hotPixelMap = new Dictionary<Coordinate, HotPixel>();
        private List<HotPixel> _hotPixels;

        private List<NodedSegmentString> _snappedResult;

        /// <summary>
        /// Initializes a new instance of the <see cref="SimpleSnapRounder"/> class.
        /// </summary>
        /// <param name="pm">The <see cref="PrecisionModel" /> to use.</param>
        public SimpleSnapRounder(PrecisionModel pm)
        {
            _pm = pm;
            _scaleFactor = pm.Scale;
        }

        /// <summary>
        /// Returns a <see cref="IList"/> of fully noded <see cref="ISegmentString"/>s.
        /// The <see cref="ISegmentString"/>s have the same context as their parent.
        /// </summary>
        /// <returns>A Collection of NodedSegmentStrings representing the substrings</returns>
        public IList<ISegmentString> GetNodedSubstrings()
        {
            return NodedSegmentString.GetNodedSubstrings(_snappedResult);
        }

        /// <summary>
        /// Computes the noding for a collection of <see cref="ISegmentString" />s.
        /// Some Noders may add all these nodes to the input <see cref="ISegmentString" />s;
        /// others may only add some or none at all.
        /// </summary>
        /// <param name="inputSegmentStrings">A collection of NodedSegmentStrings</param>
        public void ComputeNodes(IList<ISegmentString> inputSegmentStrings)
        {
            /*
             * Determine intersections at full precision.  
             * Rounding happens during Hot Pixel creation.
             */
            SnapRound(inputSegmentStrings);
#if DEBUG
            foreach (var nss in _snappedResult)
            {
                Debug.WriteLine(WKTWriter.ToLineString(nss.NodeList.GetSplitCoordinates()));
            }
#endif
        }
        /// <summary>
        ///
        /// </summary>
        /// <param name="segStrings"></param>
        private List<NodedSegmentString> SnapRound(IList<ISegmentString> segStrings)
        {
            var inputSS = CreateNodedStrings(segStrings);
            /**
             * Determine hot pixels for intersections and vertices.
             * This is done BEFORE the input lines are rounded,
             * to avoid distorting the line arrangement 
             * (rounding can cause vertices to move across edges).
             */
            var intersections = FindInteriorIntersections(inputSS);
            AddHotPixels(intersections);
            AddVertexPixels(segStrings);
            _hotPixels = new List<HotPixel>(_hotPixelMap.Values);

            var snapped = ComputeSnaps(inputSS);
            return snapped;
        }

        private static List<ISegmentString> CreateNodedStrings(IEnumerable<ISegmentString> segStrings)
        {
            var nodedStrings = new List<ISegmentString>();
            foreach (var ss in segStrings)
            {
                nodedStrings.Add(new NodedSegmentString(ss));
            }
            return nodedStrings;
        }

        private void AddVertexPixels(IEnumerable<ISegmentString> segStrings)
        {
            foreach (var nss in segStrings)
            {
                var pts = nss.Coordinates;
                AddHotPixels(pts);
            }
        }

        private void AddHotPixels(IEnumerable<Coordinate> pts)
        {
            foreach (var pt in pts)
            {
                CreateHotPixel(Round(pt));
            }
        }

        private Coordinate Round(Coordinate pt)
        {
            var p2 = pt.Copy();
            _pm.MakePrecise(p2);
            return p2;
        }

        /**
         * Gets a list of the rounded coordinates.
         * Duplicate (collapsed) coordinates are removed.
         * 
         * @param pts the coordinates to round
         * @return array of rounded coordinates
         */
        private Coordinate[] Round(IEnumerable<Coordinate> pts)
        {
            var roundPts = new CoordinateList();

            foreach (var pt in pts)
            {
                roundPts.Add(Round(pt), false);
            }
            return roundPts.ToCoordinateArray();
        }

        private HotPixel CreateHotPixel(Coordinate p)
        {
            if (_hotPixelMap.TryGetValue(p, out var hp))
                return hp;
            hp = new HotPixel(p, _scaleFactor);
            _hotPixelMap.Add(p, hp);
            return hp;
        }

        /// <summary>
        /// Computes all interior intersections in the collection of <see cref="ISegmentString" />s,
        /// and returns their <see cref="Coordinate" />s.
        /// <para/>
        /// Also adds the intersection nodes to the segments.
        /// </summary>
        /// <param name="segStrings"></param>
        /// <param name="li"></param>
        /// <returns>A list of <see cref="Coordinate" />s for the intersections.</returns>
        private IList<Coordinate> FindInteriorIntersections(IList<ISegmentString> segStrings)
        {
            var intAdder = new SnapRoundingIntersectionAdder(_pm);
            var noder = new MCIndexNoder();
            noder.SegmentIntersector = intAdder;
            noder.ComputeNodes(segStrings);
            return intAdder.Intersections;
        }

        /// <summary>
        /// Computes nodes introduced as a result of snapping segments to snap points (hot pixels).
        /// </summary>
        /// <param name="segStrings"></param>
        /// <param name="snapPts"></param>
        private List<NodedSegmentString> ComputeSnaps(IEnumerable<ISegmentString> segStrings)
        {
            var snapped = new List<NodedSegmentString>();
            foreach (NodedSegmentString ss in segStrings)
            {
                var snappedSS = ComputeSnaps(ss);
                if (snappedSS != null)
                    snapped.Add(snappedSS);
            }
            return snapped;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="ss"></param>
        private NodedSegmentString ComputeSnaps(NodedSegmentString ss)
        {
            //Coordinate[] pts = ss.getCoordinates();
            /*
             * Get edge coordinates, including added intersection nodes.
             * The coordinates are now rounded to the grid,
             * in preparation for snapping to the Hot Pixels
             */
            var pts = ss.NodedCoordinates;
            var ptsRound = Round(pts);

            // if complete collapse this edge can be eliminated
            if (ptsRound.Length <= 1)
                return null;

            // Create new nodedSS to allow adding any hot pixel nodes
            var snapSS = new NodedSegmentString(ptsRound, ss.Context);

            int snapSSindex = 0;
            for (int i = 0; i < pts.Length - 1; i++)
            {
                var currSnap = snapSS.GetCoordinate(snapSSindex);

                /*
                 * If the segment has collapsed completely, skip it
                 */
                var p1 = pts[i + 1];
                var p1Round = Round(p1);
                if (p1Round.Equals2D(currSnap))
                    continue;

                var p0 = pts[i];

                /*
                 * Add any Hot Pixel intersections with *original* segment to rounded segment.
                 * (It is important to check original segment because rounding can
                 * move it enough to intersect other hot pixels not intersecting original segment)
                 */
                SnapSegment(p0, p1, snapSS, snapSSindex);
                snapSSindex++;
            }
            return snapSS;
        }

        /// <summary>
        /// This is where all the work of snapping to hot pixels gets done
        /// (in a very inefficient brute-force way).
        /// </summary>
        private void SnapSegment(Coordinate p0, Coordinate p1, NodedSegmentString ss, int segIndex)
        {
            foreach (var hp in _hotPixels)
            {
                if (hp.Intersects(p0, p1))
                {
                    ss.AddIntersection(hp.Coordinate, segIndex);
                }
            }
        }

        /// <summary>
        /// Computes nodes introduced as a result of
        /// snapping segments to vertices of other segments.
        /// </summary>
        /// <param name="edges">The list of segment strings to snap together</param>
        [Obsolete("Will cause NotSupportedException", true)]
        public void ComputeVertexSnaps(IList<ISegmentString> edges)
        {
            throw new NotSupportedException();
        }
    }
}
