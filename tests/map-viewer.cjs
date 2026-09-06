const fs=require('fs');
const path=require('path');
const {chromium}=require('C:/Users/PC/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 try{
  const page=await browser.newPage({viewport:{width:1200,height:800}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.goto('file:///'+path.resolve(__dirname,'../view-map.html').replaceAll('\\','/'));
  const fixture={result:{simulationFrame:123,paused:true,bounds:{minX:0,maxX:200,minZ:0,maxZ:200},terrain:{samples:[{position:{x:100,z:100},waterDepth:0,groundPollutionRaw:40}]},network:{edges:[{prefab:'Small Road',curve:[{x:0,z:50},{x:60,z:50},{x:120,z:50},{x:200,z:50}]}]},zoning:{cells:[{position:{x:40,z:65},flags:'Visible Roadside',zone:3}],truncated:false},buildings:[{prefab:'Test School <safe>',index:3,version:1,position:{x:80,z:80},polygon:[{x:60,z:60},{x:100,z:60},{x:100,z:100},{x:60,z:100}],shortages:[]}],tiles:{tiles:[]}}};
  await page.locator('#mapFile').setInputFiles({name:'fixture.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(fixture))});
  await page.waitForFunction(()=>document.querySelectorAll('.building').length===1);
  if(!(await page.locator('#status').textContent()).includes('Paused snapshot'))throw Error('Snapshot status missing');
  await page.locator('.building').click();
  if(!(await page.locator('#details').textContent()).includes('Test School <safe>'))throw Error('Building inspection failed');
  const original=await page.locator('#map').getAttribute('viewBox');
  await page.mouse.move(700,400);await page.mouse.wheel(0,-100);
  if(await page.locator('#map').getAttribute('viewBox')===original)throw Error('Zoom failed');
  await page.locator('#reset').click();
  if(await page.locator('#map').getAttribute('viewBox')!==original)throw Error('Fit failed');
  await page.locator('[data-layer="Roads"]').uncheck();
  if(!(await page.locator('[id="layer-Roads"]').getAttribute('class')).includes('hidden'))throw Error('Layer toggle failed');
  const plan={steps:[{command:'build_road',args:{start:{x:20,z:150},end:{x:180,z:150}}}]};
  await page.locator('#planFile').setInputFiles({name:'plan.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(plan))});
  await page.waitForFunction(()=>document.querySelector('[id="layer-Plan"] line'));
  const downloadPromise=page.waitForEvent('download');await page.locator('#export').click();const download=await downloadPromise;
  const downloadPath=await download.path();const exported=fs.readFileSync(downloadPath,'utf8');
  if(exported.includes('layer-Roads')||!exported.includes('layer-Plan'))throw Error('Export does not respect visibility');
  await page.screenshot({path:path.resolve(__dirname,'../artifacts/map-viewer-test.png')});
  if(errors.length)throw Error(errors.join('\n'));
  console.log('PASS: map load, safe building details, zoom, fit, layers, plan overlay, SVG export; no browser errors. Synthetic fixture only.');
 }finally{await browser.close()}
})().catch(e=>{console.error(e);process.exitCode=1});
