using System;
using System.Linq;
using CitiesIIAgentBridge;
using Newtonsoft.Json.Linq;

internal static class DistrictDrawingTests
{
    internal static int Run()
    {
        int count=0;
        void Check(bool ok,string label){if(!ok)throw new Exception("FAIL: "+label);count++;Console.WriteLine("PASS: "+label);}
        void Reject(Action action,string label){bool failed=false;try{action();}catch(ArgumentException){failed=true;}Check(failed,label);}
        double[][] P(params double[] pairs)=>Enumerable.Range(0,pairs.Length/2).Select(i=>new[]{pairs[i*2],pairs[i*2+1]}).ToArray();
        var square=P(0,0,100,0,100,100,0,100);
        var closed=square.Concat(new[]{square[0]}).ToArray();
        Check(DistrictGeometry.Validate(square,32)==10000,"district area is measured in square metres");
        Check(DistrictGeometry.Validate(square.Reverse().ToArray(),32)==10000,"both windings are accepted");
        Check(DistrictGeometry.Validate(P(0,0,200,0,200,80,80,80,80,200,0,200),32)==25600,"concave district boundary is supported");
        Check(DistrictGeometry.Read(DistrictGeometry.Json(closed)).Length==4,"optional closing vertex is normalized");
        Check(DistrictGeometry.Equivalent(square,closed),"readback can include native closing vertex");
        Check(DistrictGeometry.Equivalent(square,square.Skip(2).Concat(square.Take(2)).Reverse().ToArray()),"readback may rotate and reverse corners");
        Check(!DistrictGeometry.Equivalent(square,P(0,0,101,0,100,100,0,100)),"changed boundary is detected");
        Check(!DistrictGeometry.Equivalent(square,P(0,0,100,100,100,0,0,100)),"same vertex set in crossing order is rejected");
        Check(!DistrictGeometry.Equivalent(square,P(0,0,100,0,100,100)),"missing corner is rejected");
        Check(!DistrictGeometry.Equivalent(Array.Empty<double[]>(),Array.Empty<double[]>()),"missing buffers cannot verify geometry");
        Reject(()=>DistrictGeometry.Validate(P(0,0,100,100,100,0,0,100),32),"bow-tie intersection rejected");
        Reject(()=>DistrictGeometry.Validate(P(0,0,200,0,100,0,100,100,0,100),32),"adjacent edge backtracking rejected");
        Reject(()=>DistrictGeometry.Validate(P(0,0,200,0,200,200,100,0,0,200),32),"nonadjacent vertex touching an edge rejected");
        Reject(()=>DistrictGeometry.Validate(P(0,0,100,0,100,100,0,0,0,100),32),"repeated interior corner rejected");
        Reject(()=>DistrictGeometry.Validate(P(0,0,20,0,100,100,0,100),32),"native minimum edge length enforced");
        Reject(()=>DistrictGeometry.Validate(P(0,0,100,0,200,0),32),"zero-area collinear polygon rejected");
        Reject(()=>DistrictGeometry.Read(new JArray(new JObject{["x"]="0",["z"]=0},new JObject{["x"]=100,["z"]=0},new JObject{["x"]=0,["z"]=100})),"numeric strings rejected before native tool");
        Reject(()=>DistrictGeometry.Read(DistrictGeometry.Json(P(0,0,15000,0,100,100))),"out-of-map coordinates rejected");
        Reject(()=>DistrictGeometry.Read(DistrictGeometry.Json(P(0,0,double.NaN,0,100,100))),"NaN coordinates rejected");
        Reject(()=>DistrictGeometry.Read(DistrictGeometry.Json(P(0,0,double.PositiveInfinity,0,100,100))),"infinite coordinates rejected");
        Reject(()=>DistrictGeometry.Read(new JArray()),"missing polygon rejected");
        var many=Enumerable.Range(0,65).Select(i=>new[]{1000*Math.Cos(i*2*Math.PI/65),1000*Math.Sin(i*2*Math.PI/65)}).ToArray();
        Reject(()=>DistrictGeometry.Read(DistrictGeometry.Json(many)),"over-budget corner count rejected");
        var good=new DistrictPreviewEntry{District=true,PrefabMatches=true,Create=true,Complete=true,GeometryMatches=true};
        string Validate(DistrictPreviewEntry e,bool edit=false)=>DistrictPreviewSafety.Validate(new[]{e},edit);
        Check(Validate(good)==null,"one exact district create preview accepted");
        var edit=good;edit.Create=false;edit.Modify=true;edit.HasOriginal=true;edit.OriginalMatches=true;
        Check(Validate(edit,true)==null,"exact existing-district replacement preview accepted");
        Check(Validate(edit)!=null,"stale recreation state cannot pass a create request");
        Check(Validate(good,true)!=null,"create cannot substitute for edit");
        var bad=edit;bad.OriginalMatches=false;Check(Validate(bad,true)!=null,"wrong district edit target rejected");
        bad=good;bad.Delete=true;Check(Validate(bad)!=null,"native deletion preview rejected");
        bad=good;bad.District=false;Check(Validate(bad)!=null,"non-district area preview rejected");
        bad=good;bad.PrefabMatches=false;Check(Validate(bad)!=null,"unexpected district prefab rejected");
        bad=good;bad.Complete=false;Check(Validate(bad)!=null,"open native polygon rejected");
        bad=good;bad.GeometryMatches=false;Check(Validate(bad)!=null,"native clipping or unwanted snapping rejected");
        Check(DistrictPreviewSafety.Validate(new[]{good,edit},false)!=null,"collateral neighbor edit rejected");
        Check(DistrictPreviewSafety.Validate(new[]{good,good},false)!=null,"multiple created districts rejected");
        Check(DistrictPreviewSafety.Validate(Array.Empty<DistrictPreviewEntry>(),false)!=null,"empty preview cannot pass");
        bad=edit;bad.Cancel=true;Check(DistrictPreviewSafety.Validate(new[]{bad,good},false)==null,"cancelled leftover preview does not poison current target");
        return count;
    }
}
