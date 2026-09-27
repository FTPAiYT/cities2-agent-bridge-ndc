import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const layers = ['districts', 'buildings', 'roads'];
const xml = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&apos;'}[c]));
const cell = value => String(value ?? '').replace(/[|\r\n]/g, ' ');
const csv = value => {
  let text = String(value ?? '');
  if (/^\s*[=+@-]/.test(text)) text = "'" + text;
  return '"' + text.replaceAll('"', '""') + '"';
};

export function assemble(pages) {
  if (!Array.isArray(pages) || !pages.length) throw new Error('Atlas pages required');
  const result = {};
  let first;
  for (const layer of layers) {
    const selected = pages.map(p => {
      if ('ok' in p) {
        if (p.ok !== true) throw new Error('Failed bridge response');
        return p.result;
      }
      return p;
    }).filter(p => p?.layer === layer);
    if (!selected.length) throw new Error(`Missing layer: ${layer}`);
    selected.sort((a,b) => a.offset-b.offset);
    let offset = 0; const rows = []; const ids = new Set();
    const total = selected[0].total;
    for (let i=0; i<selected.length; i++) {
      const p = selected[i];
      first ??= p;
      if (p.schemaVersion !== 1 || !p.snapshotId || !p.citySession || !Number.isInteger(p.simulationFrame) || p.paused !== true)
        throw new Error('Unsupported or unpaused snapshot');
      for (const key of ['snapshotId','citySession','simulationFrame','capturedUtc','cityName'])
        if (p[key] !== first[key]) throw new Error(`Mixed snapshot: ${key}`);
      if (p.complete !== true || !Array.isArray(p.errors) || p.errors.length) throw new Error('Incomplete atlas snapshot');
      if (!Number.isInteger(total) || total < 0 || p.total !== total || p.offset !== offset || !Array.isArray(p.items))
        throw new Error(`Missing or overlapping ${layer} page`);
      if (!Number.isInteger(p.limit) || p.limit < 1 || p.items.length > p.limit) throw new Error('Invalid page limit');
      for (const row of p.items) {
        const id = layer === 'districts' ? row.key : `${row.index}:${row.version}`;
        if (!id || ids.has(id)) throw new Error(`Duplicate or missing ${layer} ID`);
        ids.add(id); rows.push(row);
      }
      offset += p.items.length;
      const more = offset < total;
      if (offset > total || p.truncated !== more || p.nextOffset !== (more ? offset : null) ||
          (more && p.items.length === 0) || (!more && i !== selected.length-1)) throw new Error('Invalid pagination contract');
    }
    if (offset !== total) throw new Error(`Truncated ${layer} export`);
    result[layer] = rows;
  }
  if (pages.some(raw => !layers.includes((raw.result ?? raw).layer))) throw new Error('Unknown atlas layer');
  for (const [key, value] of Object.entries(first))
    if (!['items','layer','offset','limit','total','truncated','nextOffset','simulationFrameChangedSinceCapture'].includes(key)) result[key] = value;
  return result;
}

export function districtRows(atlas) {
  return atlas.districts.map(d => {
    const buildings = atlas.buildings.filter(b => b.districtKey === d.key);
    const schools = buildings.filter(b => b.serviceCapacity?.school);
    return { key:d.key, name:d.name, ...d.census, buildings:buildings.length,
      schoolSites:schools.length,
      nominalSchoolSeatsLocatedHere: schools.reduce((s,b)=>s+b.serviceCapacity.school.nominalCapacity,0),
      enrollmentAtSchoolsLocatedHere: schools.reduce((s,b)=>s+(b.serviceCapacity.school.enrolled ?? 0),0),
      schoolsWithUnknownEnrollment: schools.filter(b=>b.serviceCapacity.school.enrolled == null).length,
      services: buildings.filter(b=>b.serviceKinds?.length).length };
  });
}

export function report(atlas) {
  const lines = [`# ${cell(atlas.cityName)} — district atlas`, '',
    `Snapshot: ${cell(atlas.snapshotId)} · ${cell(atlas.capturedUtc)} · simulation frame ${atlas.simulationFrame}`, '',
    'Counts below describe residents at home and buildings physically located in each district. They do not establish service reach or additional buildings required.', '',
    '| District | Residents | Children | Teens | Adults | Seniors | School sites | Nominal seats here | Enrollment here |',
    '|---|---:|---:|---:|---:|---:|---:|---:|---:|'];
  for (const r of districtRows(atlas)) lines.push(`| ${cell(r.name)} | ${r.residents} | ${r.children} | ${r.teens} | ${r.adults} | ${r.seniors} | ${r.schoolSites} | ${r.nominalSchoolSeatsLocatedHere} | ${r.schoolsWithUnknownEnrollment ? 'Incomplete' : r.enrollmentAtSchoolsLocatedHere} |`);
  lines.push('', '## Education by level', '', '| District | School level | Enrolled residents | Nominal seats located here | Enrollment at those sites |', '|---|---|---:|---:|---:|');
  for (const d of atlas.districts) {
    for (const [index, label] of ['Elementary','High school','College','University'].entries()) {
      const level=index+1;
      const schools=atlas.buildings.filter(b=>b.districtKey===d.key && b.serviceCapacity?.school?.educationLevel===level).map(b=>b.serviceCapacity.school);
      lines.push(`| ${cell(d.name)} | ${label} | ${d.census.enrolledByLevel0To4?.[level] ?? 'Unknown'} | ${schools.reduce((sum,s)=>sum+s.nominalCapacity,0)} | ${schools.some(s=>s.enrolled==null) ? 'Incomplete' : schools.reduce((sum,s)=>sum+s.enrolled,0)} |`);
    }
  }
  lines.push('', '## Service buildings', '', '| Building | Type | Located in | Serves | School level / seats / enrolled |', '|---|---|---|---|---|');
  const names = new Map(atlas.districts.map(d=>[d.key,d.name]));
  for (const b of atlas.buildings.filter(b=>b.serviceKinds?.length)) {
    const targets = b.serviceDistricts === null ? 'Assignment unsupported' : b.serviceDistricts.length === 0 ? 'Citywide' : b.serviceDistricts.map(d=>names.get(`${d.index}:${d.version}`) ?? `Unknown district ${d.index}:${d.version}`).join(', ');
    const s = b.serviceCapacity?.school;
    lines.push(`| ${cell(b.name)} | ${cell(b.serviceKinds.join(', '))} | ${cell(names.get(b.districtKey) ?? b.districtKey)} | ${cell(targets)} | ${s ? `${s.educationLevel} / ${s.nominalCapacity} / ${s.enrolled ?? 'Unknown'}` : '—'} |`);
  }
  lines.push('', '## Interpretation', '', ...(atlas.notes ?? []).map(n=>`- ${cell(n)}`),
    '- School levels are reported separately in atlas.json (1 elementary, 2 high school, 3 college, 4 university). Summed seats across levels are inventory only.',
    '- Service assignments share capacity across their districts; citywide buildings are listed once. A district with no school inside it may still have access to a nearby school.',
    '- No exact school-shortfall calculation is made without eligible applicants, reachable alternatives and effective capacity.', '');
  return lines.join('\n');
}

export function mapSvg(atlas) {
  const points = [...atlas.districts.flatMap(d=>d.polygon ?? []), ...atlas.buildings.flatMap(b=>b.polygon ?? []), ...atlas.roads.flatMap(r=>r.curve ?? [])];
  for (const p of points) if (!Number.isFinite(p.x) || !Number.isFinite(p.z)) throw new Error('Non-finite map coordinate');
  let minX=0, maxX=1, minZ=0, maxZ=1;
  if (points.length) { minX=maxX=points[0].x; minZ=maxZ=points[0].z; }
  for (const p of points) { minX=Math.min(minX,p.x); maxX=Math.max(maxX,p.x); minZ=Math.min(minZ,p.z); maxZ=Math.max(maxZ,p.z); }
  const width=1400, height=1000, padding=55, scale=Math.min((width-padding*2)/Math.max(1,maxX-minX),(height-170)/Math.max(1,maxZ-minZ));
  const project = p => [(padding+(p.x-minX)*scale).toFixed(2),(120+(maxZ-p.z)*scale).toFixed(2)];
  const out=[`<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">`,
    `<title>${xml(atlas.cityName)} district atlas</title><rect width="100%" height="100%" fill="#f4f1e9"/>`,
    `<g font-family="Arial, sans-serif" fill="#20312e"><text x="45" y="44" font-size="28">${xml(atlas.cityName)} · District atlas</text>`,
    `<text x="45" y="72" font-size="13">${xml(atlas.capturedUtc)} · frame ${atlas.simulationFrame} · local game coordinates (+Z upward)</text>`,
    '<text x="45" y="94" font-size="13">Colored areas: districts · gray: buildings · orange: service buildings · lines: roads</text></g>'];
  const colors=['#b8d8cc','#cbd6ea','#ead8b2','#dbcae6','#cfdcb0','#eacbc3'];
  atlas.districts.forEach((d,i)=> { if (d.polygon?.length>=3) out.push(`<polygon points="${d.polygon.map(p=>project(p).join(',')).join(' ')}" fill="${colors[i%colors.length]}" fill-opacity="0.6" stroke="#6d8178" stroke-width="1.4"><title>${xml(d.name)}</title></polygon>`); });
  for (const r of atlas.roads) {
    if (r.curve?.length !== 4) throw new Error('Road requires four cubic control points');
    const [a,b,c,d]=r.curve.map(project);
    out.push(`<path d="M ${a} C ${b} ${c} ${d}" fill="none" stroke="#73817f" stroke-width="1.3"><title>${xml(r.name)}</title></path>`);
  }
  for (const b of atlas.buildings) {
    if (!b.polygon?.length) continue;
    out.push(`<polygon points="${b.polygon.map(p=>project(p).join(',')).join(' ')}" fill="${b.serviceKinds?.length ? '#ba592b' : '#8e9692'}"><title>${xml(b.name)}</title></polygon>`);
  }
  for (const d of atlas.districts) {
    if (!d.polygon?.length) continue;
    const p=d.polygon.reduce((a,p)=>({x:a.x+p.x/d.polygon.length,z:a.z+p.z/d.polygon.length}),{x:0,z:0});
    const [x,y]=project(p);
    out.push(`<text x="${x}" y="${y}" text-anchor="middle" font-family="Arial, sans-serif" font-size="14" fill="#172b24" stroke="#f4f1e9" stroke-width="3" paint-order="stroke">${xml(d.name)} · ${d.census.residents}</text>`);
  }
  out.push('</svg>'); return out.join('\n');
}

export function writeExport(atlas, directory) {
  // Render and validate before creating any destination. Never overwrite another export.
  const rows = districtRows(atlas);
  const keys=['key','name','residents','children','teens','adults','seniors','homelessResidents','sickResidents','injuredResidents','schoolSites','nominalSchoolSeatsLocatedHere','enrollmentAtSchoolsLocatedHere','schoolsWithUnknownEnrollment'];
  const files={ 'atlas.json':JSON.stringify(atlas,null,2)+'\n', 'report.md':report(atlas), 'map.svg':mapSvg(atlas),
    'districts.csv':keys.map(csv).join(',')+'\r\n'+rows.map(r=>keys.map(k=>csv(r[k])).join(',')).join('\r\n')+'\r\n' };
  fs.mkdirSync(directory); // An existing directory is an error, including an empty one.
  for (const [name, content] of Object.entries(files)) fs.writeFileSync(path.join(directory,name),content,{flag:'wx'});
  return Object.keys(files);
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  if (process.argv.length !== 4) throw new Error('Usage: node atlas/export.mjs pages.json NEW_OUTPUT_DIRECTORY');
  const atlas = assemble(JSON.parse(fs.readFileSync(process.argv[2],'utf8').replace(/^\uFEFF/,'')));
  console.log(writeExport(atlas,path.resolve(process.argv[3])).join('\n'));
}
