using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CitiesIIAgentBridge
{
    internal static class DistrictGeometry
    {
        internal static double[][] Read(JToken token)
        {
            if (!(token is JArray rows) || rows.Count < 3 || rows.Count > 65) throw new ArgumentException("district_polygon_requires_3_to_64_corners");
            var points = new List<double[]>();
            foreach (var row in rows)
            {
                if (!(row is JObject) || !Number(row["x"]) || !Number(row["z"])) throw new ArgumentException("district_corner_requires_numeric_x_z");
                double x = (double)row["x"], z = (double)row["z"];
                if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(z) || double.IsInfinity(z) || Math.Abs(x) > 14000 || Math.Abs(z) > 14000)
                    throw new ArgumentException("district_corner_out_of_bounds");
                points.Add(new[] { x, z });
            }
            if (points.Count > 3 && Distance(points[0], points[points.Count - 1]) < 0.001) points.RemoveAt(points.Count - 1);
            if (points.Count > 64) throw new ArgumentException("district_polygon_requires_3_to_64_corners");
            return points.ToArray();
        }
        private static bool Number(JToken t) => t?.Type == JTokenType.Integer || t?.Type == JTokenType.Float;
        internal static double Distance(double[] a, double[] b) => Math.Sqrt((a[0]-b[0])*(a[0]-b[0])+(a[1]-b[1])*(a[1]-b[1]));
        private static double Cross(double[] a,double[] b,double[] c) => (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]);
        private static bool On(double[] a,double[] b,double[] p) => Math.Abs(Cross(a,b,p)) < 0.000001 && p[0]>=Math.Min(a[0],b[0])-0.000001 && p[0]<=Math.Max(a[0],b[0])+0.000001 && p[1]>=Math.Min(a[1],b[1])-0.000001 && p[1]<=Math.Max(a[1],b[1])+0.000001;
        private static bool Intersects(double[] a,double[] b,double[] c,double[] d)
        {
            double abC=Cross(a,b,c),abD=Cross(a,b,d),cdA=Cross(c,d,a),cdB=Cross(c,d,b);
            return (abC*abD<0 && cdA*cdB<0) || On(a,b,c) || On(a,b,d) || On(c,d,a) || On(c,d,b);
        }
        internal static double Validate(double[][] points,double minimumEdge)
        {
            if (points.Length < 3 || points.Length > 64 || minimumEdge <= 0 || double.IsNaN(minimumEdge) || double.IsInfinity(minimumEdge)) throw new ArgumentException("invalid_district_geometry_contract");
            double area=0;
            for(int i=0;i<points.Length;i++)
            {
                var a=points[i]; var b=points[(i+1)%points.Length]; var c=points[(i+2)%points.Length];
                if(Distance(a,b)<minimumEdge) throw new ArgumentException("district_edge_below_native_minimum");
                if(Math.Abs(Cross(a,b,c)) < 0.000001 && (b[0]-a[0])*(c[0]-b[0])+(b[1]-a[1])*(c[1]-b[1]) <= 0)
                    throw new ArgumentException("district_polygon_backtracks");
                area+=a[0]*b[1]-b[0]*a[1];
                for(int j=i+1;j<points.Length;j++)
                {
                    if(j==i+1 || (i==0 && j==points.Length-1)) continue;
                    if(Intersects(a,b,points[j],points[(j+1)%points.Length])) throw new ArgumentException("district_polygon_self_intersects");
                }
            }
            area=Math.Abs(area)/2;
            if(area<64) throw new ArgumentException("district_polygon_area_below_64_square_metres");
            return area;
        }
        // Native geometry may reverse winding, rotate the first corner or retain a closing vertex.
        internal static bool Equivalent(double[][] a,double[][] b,double tolerance=0.05)
        {
            int na=a.Length,nb=b.Length;
            if(na>1 && Distance(a[0],a[na-1])<=tolerance)na--;
            if(nb>1 && Distance(b[0],b[nb-1])<=tolerance)nb--;
            if(na!=nb || na<3)return false;
            for(int start=0;start<nb;start++) foreach(int direction in new[]{-1,1})
            {
                bool same=true;
                for(int i=0;i<na && same;i++) same=Distance(a[i],b[(start+direction*i+nb)%nb])<=tolerance;
                if(same)return true;
            }
            return false;
        }
        internal static JArray Json(double[][] points) => new JArray(points.Select(p=>new JObject { ["x"]=p[0],["z"]=p[1] }));
    }

    internal struct DistrictPreviewEntry
    {
        internal bool District, PrefabMatches, HasOriginal, OriginalMatches, Create, Modify, Delete, Cancel, Complete, GeometryMatches;
    }
    internal static class DistrictPreviewSafety
    {
        internal static string Validate(IEnumerable<DistrictPreviewEntry> entries,bool editing)
        {
            int count=0;
            foreach(var e in entries)
            {
                if(e.Cancel)continue;
                if(e.Delete)return "district_preview_deletes_an_area";
                if(!e.District || !e.PrefabMatches)return "district_preview_changes_another_area";
                if(editing ? (!e.HasOriginal || !e.OriginalMatches || !e.Modify || e.Create) : (e.HasOriginal || !e.Create || e.Modify))
                    return "district_preview_target_mismatch";
                if(!e.Complete || !e.GeometryMatches)return "district_preview_boundary_mismatch";
                count++;
            }
            return count==1 ? null : "district_preview_expected_one_area";
        }
    }
}
