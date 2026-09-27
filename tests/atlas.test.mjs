import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn } from 'node:child_process';
import { assemble, mapSvg, report, writeExport, districtRows } from '../atlas/export.mjs';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const clone=x=>JSON.parse(JSON.stringify(x));
export function fixture() {
  const common={schemaVersion:1,snapshotId:'synthetic-snapshot',citySession:'synthetic-city',simulationFrame:42,capturedUtc:'2026-09-23T00:00:00Z',cityName:'SYNTHETIC TEST CITY',paused:true,complete:true,errors:[],notes:['Synthetic fixture; not live city data.']};
  const polygon=[{x:0,z:0},{x:200,z:0},{x:200,z:200},{x:0,z:200}];
  const census={residents:4,children:1,teens:1,adults:1,seniors:1,homelessResidents:0,sickResidents:0,injuredResidents:0};
  const districts=[{key:'10:1',name:'North <&>',polygon,census},{key:'20:1',name:'South',polygon:polygon.map(p=>({...p,x:p.x+210})),census}];
  const buildings=[{index:100,version:1,name:'Shared school',districtKey:'10:1',polygon:polygon.map(p=>({x:p.x/10+30,z:p.z/10+30})),serviceKinds:['school'],serviceDistricts:[{index:10,version:1},{index:20,version:1}],serviceCapacity:{school:{educationLevel:1,nominalCapacity:100,enrolled:60}}}];
  const roads=[{index:200,version:1,name:'Curve',curve:[{x:0,z:0},{x:100,z:0},{x:100,z:200},{x:200,z:200}]}];
  return Object.entries({districts,buildings,roads}).flatMap(([layer,rows])=>rows.map((row,i)=>({...common,layer,offset:i,limit:1,total:rows.length,items:[row],truncated:i+1<rows.length,nextOffset:i+1<rows.length?i+1:null})));
}

test('complete multipage capture assembles all three layers',()=> {
  const a=assemble(fixture()); assert.equal(a.districts.length,2); assert.equal(a.buildings.length,1); assert.equal(a.roads.length,1);
});
test('bridge response envelopes are supported',()=>assert.equal(assemble(fixture().map(result=>({ok:true,result}))).snapshotId,'synthetic-snapshot'));
test('missing continuation fails instead of silently truncating',()=>assert.throws(()=>assemble(fixture().filter((_,i)=>i!==1)),/Truncated/));
test('mixed snapshots, city sessions and frames fail',()=> {
  for (const key of ['snapshotId','citySession','simulationFrame']) { const p=fixture(); p[1][key]=key==='simulationFrame'?43:'other'; assert.throws(()=>assemble(p),/Mixed/); }
});
test('duplicate identities and overlapping pages fail',()=> {
  let p=fixture(); p[1].items[0].key=p[0].items[0].key; assert.throws(()=>assemble(p),/Duplicate/);
  p=fixture(); p[1].offset=0; assert.throws(()=>assemble(p),/overlapping/);
});
test('zero-length nonterminal page and incorrect next offset fail',()=> {
  let p=fixture(); p[0].items=[]; assert.throws(()=>assemble(p),/pagination/);
  p=fixture(); p[0].nextOffset=7; assert.throws(()=>assemble(p),/pagination/);
});
test('failed bridge, partial capture and running simulation fail',()=> {
  assert.throws(()=>assemble([{ok:false}]),/Failed/);
  let p=fixture(); p[0].complete=false; assert.throws(()=>assemble(p),/Incomplete/);
  p=fixture(); p[0].paused=false; assert.throws(()=>assemble(p),/unpaused/);
});
test('shared school capacity is counted once at its physical location',()=> {
  const a=assemble(fixture()); const rows=districtRows(a);
  assert.equal(rows[0].nominalSchoolSeatsLocatedHere,100); assert.equal(rows[1].nominalSchoolSeatsLocatedHere,0);
  assert.match(report(a),/North <&>, South/); assert.match(report(a),/No exact school-shortfall/);
});
test('unknown enrollment remains explicitly unknown in report',()=> {
  const a=assemble(fixture()); a.buildings[0].serviceCapacity.school.enrolled=null;
  assert.match(report(a),/Incomplete/); assert.match(report(a),/100 \/ Unknown/);
});
test('citywide and unsupported assignments stay distinct',()=> {
  const a=assemble(fixture()); a.buildings[0].serviceDistricts=[]; assert.match(report(a),/Citywide/);
  a.buildings[0].serviceDistricts=null; assert.match(report(a),/Assignment unsupported/);
});
test('SVG escapes names and preserves cubic curves',()=> {
  const a=assemble(fixture()); a.cityName='<script>alert(1)</script>';
  const svg=mapSvg(a); assert.ok(!svg.includes('<script>')); assert.match(svg,/&lt;script&gt;/); assert.match(svg,/North &lt;&amp;&gt;/); assert.match(svg,/<path d="M .* C /);
});
test('invalid geometry fails before files are created',()=> {
  const a=assemble(fixture()); a.roads[0].curve[1].x=Infinity; assert.throws(()=>mapSvg(a),/Non-finite/);
});
test('empty layers and no districts produce valid empty map',()=> {
  const p=['districts','buildings','roads'].map(layer=>({...fixture()[0],layer,total:0,items:[],truncated:false,nextOffset:null}));
  assert.match(mapSvg(assemble(p)),/<svg/);
});
test('export creates four files, escapes CSV formulas, refuses overwrite',()=> {
  const temporary=fs.mkdtempSync(path.join(os.tmpdir(),'atlas-export-test-')); const out=path.join(temporary,'export');
  const a=assemble(fixture()); a.districts[0].name='=1+1'; writeExport(a,out);
  assert.equal(fs.readdirSync(out).length,4); assert.match(fs.readFileSync(path.join(out,'districts.csv'),'utf8'),/'=1\+1/);
  assert.throws(()=>writeExport(a,out),/EEXIST/);
});

test('real PowerShell export follows one snapshot through isolated fake mailbox', {timeout:25000}, async()=> {
  const tmp=fs.mkdtempSync(path.join(os.tmpdir(),'atlas-mailbox-test-'));
  // Copy the real scripts so no active gameplay journal can be touched by this fake test.
  fs.mkdirSync(path.join(tmp,'atlas')); fs.copyFileSync(path.join(root,'bridge.ps1'),path.join(tmp,'bridge.ps1'));
  fs.copyFileSync(path.join(root,'export-atlas.ps1'),path.join(tmp,'export-atlas.ps1'));
  fs.copyFileSync(path.join(root,'atlas/export.mjs'),path.join(tmp,'atlas/export.mjs'));
  const mailbox=path.join(tmp,'mailbox'); fs.mkdirSync(mailbox); fs.mkdirSync(path.join(mailbox,'requests')); fs.mkdirSync(path.join(mailbox,'responses'));
  fs.writeFileSync(path.join(mailbox,'session.json'),JSON.stringify({status:'ready',heartbeatUtc:new Date().toISOString(),session:'fake-session',citySession:'synthetic-city'}));
  const pages=fixture(); const seen=[]; let error;
  const timer=setInterval(()=> {
    try { for (const filename of fs.readdirSync(path.join(mailbox,'requests')).filter(f=>f.endsWith('.json'))) {
      if (seen.includes(filename)) continue;
      const request=JSON.parse(fs.readFileSync(path.join(mailbox,'requests',filename),'utf8')); seen.push(filename);
      assert.equal(request.command,'get_district_atlas');
      if (seen.length>1) assert.equal(request.args.snapshotId,'synthetic-snapshot');
      const page=pages.find(p=>p.layer===request.args.layer && p.offset===request.args.offset); assert.ok(page);
      fs.writeFileSync(path.join(mailbox,'responses',filename+'.tmp'),JSON.stringify({id:request.id,ok:true,result:page}));
      fs.renameSync(path.join(mailbox,'responses',filename+'.tmp'),path.join(mailbox,'responses',filename));
    } } catch(e) { error=e; }
  },30);
  const process=spawn('pwsh',['-NoProfile','-File',path.join(tmp,'export-atlas.ps1'),'-Capture','-MailboxPath',mailbox,'-OutputDirectory',path.join(tmp,'result')],{windowsHide:true});
  let output=''; process.stdout.on('data',d=>output+=d); process.stderr.on('data',d=>output+=d);
  const deadline=setTimeout(()=>process.kill(),20000);
  try { const code=await new Promise((resolve,reject)=>{process.on('exit',resolve);process.on('error',reject);});
    if(error) throw error;
    assert.equal(code,0,output); assert.equal(seen.length,4);
    const atlas=JSON.parse(fs.readFileSync(path.join(tmp,'result/atlas.json'),'utf8'));
    assert.equal(atlas.districts.length,2); assert.equal(atlas.roads.length,1);
  } finally { clearInterval(timer); clearTimeout(deadline); if(process.exitCode===null)process.kill(); }
});
