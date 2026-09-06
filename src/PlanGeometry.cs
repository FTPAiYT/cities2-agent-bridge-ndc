using System;
using System.Linq;

namespace CitiesIIAgentBridge
{
    internal static class PlanGeometry
    {
        // Separating-axis test for convex lot polygons. Touching edges are allowed.
        internal static bool Overlap(double[][] a,double[][] b)
        {
            foreach(var polygon in new[]{a,b}) for(int i=0;i<polygon.Length;i++)
            {
                var p=polygon[i];var q=polygon[(i+1)%polygon.Length]; double nx=-(q[1]-p[1]),nz=q[0]-p[0];
                var ap=a.Select(v=>v[0]*nx+v[1]*nz).ToArray();var bp=b.Select(v=>v[0]*nx+v[1]*nz).ToArray();
                if(ap.Max()<=bp.Min()+0.001 || bp.Max()<=ap.Min()+0.001) return false;
            }
            return true;
        }
        internal static double[][] Corridor(double ax,double az,double bx,double bz,double halfWidth)
        {
            double length=Math.Sqrt((bx-ax)*(bx-ax)+(bz-az)*(bz-az));
            if(length<0.01) throw new ArgumentException("zero_length_segment");
            double nx=-(bz-az)/length*halfWidth,nz=(bx-ax)/length*halfWidth;
            return new[]{new[]{ax+nx,az+nz},new[]{bx+nx,bz+nz},new[]{bx-nx,bz-nz},new[]{ax-nx,az-nz}};
        }
    }
}
