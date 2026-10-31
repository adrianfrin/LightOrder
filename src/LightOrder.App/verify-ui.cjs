// Hardware-free behavioral tests. No native bridge, service, process policy or hardware calls.
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm'),assert=require('node:assert/strict');
const context=vm.createContext({console,structuredClone,Map,Set,Date,Number,JSON,Error,Promise,crypto:require('node:crypto').webcrypto,setTimeout:()=>0,clearTimeout(){},setInterval(){},window:{},document:coolingDocument()});
for(const name of ['icons.js','charts.js','editor.js','automation.js','app.js']){let text=fs.readFileSync(path.join(__dirname,'Assets',name),'utf8');if(name==='app.js')text=text.slice(0,text.lastIndexOf("document.querySelectorAll('[data-page]')"));vm.runInContext(text,context,{filename:name})}
const production=vm.runInContext('({render,refresh,persist,execute})',context);
vm.runInContext('render=()=>{};toast=()=>{};refresh=async()=>{};',context);
const evaluate=source=>vm.runInContext(source,context);
// A small DOM fixture: render actual markup, bind production handlers, then dispatch events.
// Missing selectors return null so the manual -> custom regression cannot be masked.
function coolingDocument(){
  const nodes=[];
  function makeNode(tagName,attributes={}){
    const node={tagName:tagName.toUpperCase(),attributes,dataset:{},children:[],style:{setProperty(){}},value:attributes.value??'',open:false,hidden:'hidden' in attributes,disabled:'disabled' in attributes,textContent:'',listeners:{}};
    for(const [key,value] of Object.entries(attributes))if(key.startsWith('data-'))node.dataset[key.slice(5).replace(/-([a-z])/g,(_,c)=>c.toUpperCase())]=value;
    const classes=new Set((attributes.class||'').split(/\s+/).filter(Boolean));
    node.classList={add:name=>classes.add(name),remove:name=>classes.delete(name),contains:name=>classes.has(name),toggle(name,force){const on=force??!classes.has(name);on?classes.add(name):classes.delete(name);return on}};
    node.setAttribute=(key,value)=>{attributes[key]=String(value)};node.hasAttribute=key=>key in attributes;node.removeAttribute=key=>{delete attributes[key]};
    node.addEventListener=(type,handler)=>{(node.listeners[type]??=[]).push(handler)};
    node.dispatch=type=>{const event={target:node,currentTarget:node,preventDefault(){}};node['on'+type]?.(event);for(const handler of node.listeners[type]||[])handler(event)};
    node.focus=()=>{doc.activeElement=node};
    const descendants=()=>node.children.flatMap(child=>[child,...child.descendants()]);node.descendants=descendants;
    node.querySelectorAll=selector=>descendants().filter(child=>selector.split(',').some(part=>matches(child,part.trim())));node.querySelector=selector=>node.querySelectorAll(selector)[0]||null;
    Object.defineProperty(node,'innerHTML',{set(html){node.children=[];const stack=[node];for(const match of html.matchAll(/<\/?([\w-]+)\b([^>]*)>/g)){const [text,tag,raw]=match;if(text.startsWith('</')){if(stack.length>1)stack.pop();continue}const attrs={};for(const attr of raw.matchAll(/([^\s=]+)(?:="([^"]*)")?/g))attrs[attr[1]]=attr[2]??'';const child=makeNode(tag,attrs);stack.at(-1).children.push(child);if(!['input','img','br','hr','meta','link'].includes(tag)&&!text.endsWith('/>'))stack.push(child)}}});
    return node;
  }
  function matches(node,selector){if(selector.startsWith('#'))return node.attributes.id===selector.slice(1);if(selector.startsWith('.'))return node.classList.contains(selector.slice(1));const attr=selector.match(/^\[([^=\]]+)(?:="([^"]*)")?\]$/);if(attr)return node.hasAttribute(attr[1])&&(attr[2]===undefined||node.attributes[attr[1]]===attr[2]);return node.tagName.toLowerCase()===selector}
  const doc={activeElement:null,documentElement:{dataset:{},style:{}},getElementById:id=>doc.querySelector('#'+id),querySelectorAll:selector=>nodes.flatMap(node=>[node,...node.descendants()]).filter(node=>selector.split(',').some(part=>matches(node,part.trim()))),querySelector:selector=>doc.querySelectorAll(selector)[0]||null};
  for(const id of ['main','modal','brand-name','brand-subtitle'])nodes.push(makeNode('div',{id}));
  return doc;
}
// FanPoint declaration order is Temperature, Duty; Wire.Json emits camelCase names.
// Use a JSON boundary and duty-first input to reproduce a service roundtrip's order change.
function wireFanProfile(profile){return JSON.parse(JSON.stringify({mode:profile.mode,radiatorPercent:profile.radiatorPercent,casePercent:profile.casePercent,radiatorCurve:profile.radiatorCurve.map(p=>({temperature:p.temperature,duty:p.duty})),caseCurve:profile.caseCurve.map(p=>({temperature:p.temperature,duty:p.duty}))}))}
(async()=>{
  evaluate(`globalThis.profile={mode:'curve',radiatorPercent:40,casePercent:25,radiatorCurve:[{temperature:40,duty:40},{temperature:80,duty:100}],caseCurve:[{temperature:40,duty:25},{temperature:80,duty:100}]};validateProfile(profile);`);
  assert.throws(()=>evaluate('validateProfile({...profile,radiatorPercent:NaN})'));
  assert.throws(()=>evaluate('validateProfile({...profile,caseCurve:[{temperature:50,duty:50},{temperature:40,duty:40},{temperature:80,duty:100}]})'));
  assert.throws(()=>evaluate('validateProfile({...profile,radiatorCurve:[{temperature:40,duty:40},{temperature:80,duty:99}]})'));
  assert.equal(evaluate('finite(null)'),false);assert.equal(evaluate('finite(Infinity)'),false);
  evaluate(`globalThis.now=Date.now();ingest({devices:[],telemetry:[{deviceId:'neutral',name:'sensor',type:'temperature',unit:'°C',timestamp:new Date(now-2000).toISOString(),value:40},{deviceId:'neutral',name:'sensor',type:'temperature',unit:'°C',timestamp:new Date(now-1000).toISOString(),value:null},{deviceId:'neutral',name:'sensor',type:'temperature',unit:'°C',timestamp:new Date(now).toISOString(),value:45}],diagnostics:[]});`);
  const chart=evaluate("historyPanels('temperature')");assert.ok(!chart.includes('NaN'));assert.ok(chart.includes('View sample data')||chart.includes('查看采样数据'));assert.ok(chart.includes('M48.00 52.83 M468.00 38.18'));
  evaluate(`rpc=async()=>({status:'accepted',detail:'unverified'});`);
  await evaluate("execute('neutral',Capabilities.LightingStatic,{})");
  await assert.rejects(()=>evaluate("execute('neutral',Capabilities.FanProfile,{})"));
  evaluate("rpc=async()=>({status:'partial',detail:'one device failed'});");await assert.rejects(()=>evaluate("execute('neutral',Capabilities.LightingStatic,{})"));
  evaluate(`globalThis.calls=[];globalThis.saved=[];state.connected=true;state.snapshot={devices:[{id:'a',name:'Device A',capabilities:[Capabilities.LightingStatic,Capabilities.LightingOff]},{id:'b',name:'Device B',capabilities:[Capabilities.LightingStatic,Capabilities.LightingOff]}],telemetry:[],diagnostics:[]};globalThis.original={devices:{a:{effect:'static',color:'#112233',brightness:.25},b:{effect:'static',color:'#445566',brightness:.5}},sync:false,members:[],independent:{},group:{effect:'static',color:'#778899',brightness:.35}};state.savedLighting=clone(original);state.lighting=clone(original);state.lighting.sync=true;state.lighting.members=['a','b'];state.lighting.independent=clone(original.devices);state.lightingDirty=true;persist=async extra=>{saved.push(clone(extra));state.prefs={...state.prefs,...extra}};execute=async(id,cap,p)=>{calls.push({id,cap,p});if(id==='b')throw new Error('offline');return {status:'accepted'}};`);
  await evaluate('applyLighting()');assert.equal(evaluate('state.lightingDirty'),true);assert.equal(evaluate('state.errors.length'),1);assert.equal(evaluate('state.savedLighting.sync'),false);assert.equal(evaluate("saved[0].pendingLighting.independent.a.color"),'#112233');assert.equal(evaluate('calls.length'),2);
  evaluate(`execute=async(id,cap,p)=>{calls.push({id,cap,p});return {status:'accepted'}};`);await evaluate('applyLighting()');assert.equal(evaluate('state.lightingDirty'),false);assert.equal(evaluate('state.savedLighting.sync'),true);assert.equal(evaluate('state.prefs.pendingLighting'),null);
  evaluate(`calls=[];state.lighting=clone(original);state.lightingDirty=true;`);await evaluate('applyLighting()');assert.equal(evaluate('calls.length'),2);assert.equal(evaluate('calls[0].p.color'),'#112233');assert.equal(evaluate('calls[1].p.color'),'#445566');
  evaluate(`state.errors=[];state.lightingDirty=true;state.lighting.devices.a.color='#abcdef';persist=async()=>{throw new Error('preferences write failed')};calls=[];`);await evaluate('applyLighting()');assert.equal(evaluate('calls.length'),0);assert.equal(evaluate('state.lightingDirty'),true);assert.ok(evaluate('state.errors[0]').includes('preferences write failed'));
  evaluate(`state.lighting=clone(original);state.lighting.sync=true;state.lighting.members=['a','b'];state.lighting.independent=clone(original.devices);globalThis.exited=false;execute=async id=>{if(id==='b')throw new Error('restore failed')};rpc=async method=>{if(method==='window.exit')exited=true};`);await evaluate('releaseAndExit()');assert.equal(evaluate('exited'),false);assert.equal(evaluate('state.errors.length'),1);
  console.log('PASS: FanProfile guards, finite/null telemetry gap, command result semantics, sync opt-in snapshots, partial failure, independent restore, preference failure before writes, failed release blocks exit.');
  evaluate(`globalThis.dynamic={effect:Effects.Breathing,color:'#007AFF',brightness:.6,periodSeconds:8};`);
  assert.equal(evaluate('validLight(dynamic)'),true);assert.equal(evaluate('normalizeLight(dynamic).periodSeconds'),8);
  for(const expression of ['{...dynamic,periodSeconds:1}','{...dynamic,periodSeconds:61}','{...dynamic,periodSeconds:NaN}','{...dynamic,periodSeconds:undefined}','{...dynamic,effect:"shell"}'])assert.equal(evaluate(`validLight(${expression})`),false);
  assert.equal(evaluate("normalizeLight({effect:'static',color:'#007AFF',brightness:1}).periodSeconds"),6);
  evaluate(`state.snapshot.devices.forEach(d=>{d.capabilities.push(Capabilities.LightingEffect);d.metadata={effects:['static','breathing','rainbow','off']}});state.lighting=clone(original);state.lighting.sync=true;state.lighting.members=['a','b'];state.lighting.independent=clone(original.devices);state.lighting.group=clone(dynamic);state.savedLighting=clone(original);state.lightingDirty=true;calls=[];saved=[];persist=async extra=>{saved.push(clone(extra));state.prefs={...state.prefs,...extra}};execute=async(id,cap,p)=>{calls.push({id,cap,p});return {status:'accepted'}};`);
  await evaluate('applyLighting()');assert.equal(evaluate('calls.length'),2);assert.equal(evaluate('calls[0].cap'),'lighting.effect');assert.equal(evaluate('calls[0].p.periodSeconds'),8);assert.equal(evaluate('calls[0].p.effect'),'breathing');assert.equal(evaluate('calls[0].p.phaseUtc'),evaluate('calls[1].p.phaseUtc'));assert.notEqual(evaluate('calls[0].p.operationId'),evaluate('calls[1].p.operationId'));assert.equal(evaluate('state.savedLighting.group.periodSeconds'),8);assert.equal(evaluate('state.prefs.pendingLighting'),null);
  await evaluate("saveScene('Breathing scene')");assert.equal(evaluate('savedScenes()[0].settings.a.periodSeconds'),8);evaluate('loadScene(savedScenes()[0])');assert.equal(evaluate('state.lighting.devices.a.periodSeconds'),8);assert.equal(evaluate('state.lighting.sync'),false);
  assert.equal(evaluate("supportsEffect({...deviceById('a'),metadata:{effects:['static']}},Effects.Breathing)"),false);
  evaluate(`globalThis.rule={id:'rule-1',name:'Evening',enabled:false,trigger:{kind:TriggerKinds.Daily,time:'20:30',days:[1,2,3,4,5]},commands:[lightCommand('a',dynamic)]};validateRule(rule);`);
  for(const expression of ["{...rule,trigger:{...rule.trigger,time:'25:00'}}","{...rule,trigger:{...rule.trigger,days:[]}}","{...rule,trigger:{...rule.trigger,days:[1,1]}}","{...rule,trigger:{...rule.trigger,days:[7]}}","{...rule,commands:[]}","{...rule,commands:[{deviceId:'a',capability:'shell.execute',parameters:{command:'whoami'}}]}","{...rule,commands:[{...rule.commands[0],parameters:{...rule.commands[0].parameters,command:'whoami'}}]}","{...rule,commands:[{...rule.commands[0],parameters:{...rule.commands[0].parameters,effect:'static'}}]}"]){assert.throws(()=>evaluate(`validateRule(${expression})`),expression)}
  evaluate(`globalThis.processRule={...rule,id:'rule-2',trigger:{kind:TriggerKinds.Process,processName:'Game.exe'},commands:[{deviceId:'windows',capability:Capabilities.ProcessPolicy,parameters:{priority:'High',affinityMask:'0xF'}}]};validateRule(processRule);`);
  for(const name of ['C:\\Games\\game.exe','*.exe','game',' game.exe','cmd.exe /c task'])assert.equal(evaluate(`exactExe(${JSON.stringify(name)})`),false,name);
  for(const expression of ["{...processRule,commands:[{...processRule.commands[0],parameters:{priority:'Realtime'}}]}","{...processRule,commands:[{...processRule.commands[0],parameters:{priority:'Normal',pid:42}}]}","{...processRule,commands:[{...processRule.commands[0],parameters:{priority:'Normal',affinityMask:'0x0'}}]}","{...processRule,exitCommands:processRule.commands}"]){assert.throws(()=>evaluate(`validateRule(${expression})`),expression)}
  assert.throws(()=>evaluate('validateAutomation({schemaVersion:1,rules:[rule,rule]})'));
  assert.throws(()=>evaluate("validateRule({...rule,exitCommands:rule.commands})"));assert.throws(()=>evaluate('validateRule({...rule,commands:Array(17).fill(rule.commands[0])})'));assert.equal(evaluate("exactExe('....exe')"),false);
  evaluate(`state.prefs.appliedLighting={a:clone(dynamic)};state.snapshot.devices.push({id:'policy',capabilities:[Capabilities.PowerWindowsScheme],metadata:{power:{activeSchemeId:'balanced',schemes:[{id:'balanced',name:'Balanced',active:true}]}}});globalThis.restoreCommands=captureExitCommands([lightCommand('a',dynamic),{deviceId:'policy',capability:Capabilities.PowerWindowsScheme,parameters:{schemeId:'performance'}},processRule.commands[0]]);`);
  assert.equal(evaluate('restoreCommands.length'),2);assert.equal(evaluate('restoreCommands[0].parameters.periodSeconds'),8);assert.equal(evaluate('restoreCommands[1].parameters.schemeId'),'balanced');assert.throws(()=>evaluate('captureExitCommands([processRule.commands[0]])'));assert.throws(()=>evaluate("captureExitCommands([{deviceId:'missing',capability:Capabilities.FanProfile,parameters:profile}])"));
  evaluate(`state.automationLoaded=true;state.automationError='';globalThis.automationCalls=[];rpc=async(method,params)=>{automationCalls.push({method,params:clone(params)});return {schemaVersion:1,rules:clone(params?.rules||[rule]),status:[{ruleId:'rule-1',status:'idle'}]}};`);
  await evaluate('writeAutomation([rule])');assert.equal(evaluate('automationCalls[0].method'),'app.automation.write');assert.deepEqual(JSON.parse(evaluate('JSON.stringify(Object.keys(automationCalls[0].params).sort())')),['rules','schemaVersion']);assert.equal(evaluate('automationCalls[1].method'),'app.automation.read');assert.equal(evaluate('state.automation.status[0].ruleId'),'rule-1');
  evaluate(`rpc=async()=>{throw new Error('write unavailable')};`);await assert.rejects(()=>evaluate('writeAutomation([processRule])'));assert.equal(evaluate('state.automation.rules[0].id'),'rule-1');
  evaluate(`state.theme=Themes.Dark;applyTheme()`);assert.equal(evaluate('document.documentElement.dataset.theme'),'dark');evaluate('state.theme=Themes.Light;applyTheme()');assert.equal(evaluate('document.documentElement.dataset.theme'),'light');evaluate(`state.theme=Themes.Auto;window.matchMedia=()=>({matches:true});applyTheme()`);assert.equal(evaluate('document.documentElement.dataset.theme'),'dark');
  evaluate(`state.snapshot.devices.push({id:'fan',capabilities:[Capabilities.FanProfile],metadata:{profile,session:{active:true,status:'active',expiresUtc:null}}});state.fanDraft=clone(profile);state.fanSelected='fan';state.prefs={};`);assert.ok(evaluate('fanEditor(selectedFan()).includes("Continuous control")||fanEditor(selectedFan()).includes("持续控制")'));
  evaluate("selectedFan().metadata.session.active=false;selectedFan().metadata.session.status='ready'");assert.ok(!evaluate('fanEditor(selectedFan())').includes('Continuous control'));assert.ok(!evaluate('fanEditor(selectedFan())').includes('持续控制'));
  assert.ok(!evaluate('overview()').includes('scene-hero'));assert.ok(evaluate('settings()').includes('restore-cooling-startup'));assert.ok(evaluate('settings()').includes('aria-checked="false"'));
  console.log('PASS: Dynamic effect validation/persistence, shared batch phase, scene roundtrip, capability filtering, automation schedule/process validation, shell rejection, process identity guards, service write shape/failure, themes, continuous fan session and default-off startup opt-ins.');

  context.wireFanProfile=wireFanProfile;
  context.production=production;
  evaluate(`globalThis.dutyFirst=p=>({...clone(p),radiatorCurve:p.radiatorCurve.map(({temperature,duty})=>({duty,temperature})),caseCurve:p.caseCurve.map(({temperature,duty})=>({duty,temperature}))});
    state.page='cooling';state.language='zh-CN';state.connected=true;state.busy=false;state.errors=[];state.lightingDirty=false;state.fanSelected='fan';state.fanTarget='radiator';state.fanDirty=false;
    state.snapshot={version:'1.0',devices:[{id:'fan',name:'Test fan',capabilities:[Capabilities.FanProfile],metadata:{profile:wireFanProfile(FanPresets.balanced),session:{status:'ready'}}}],telemetry:[],diagnostics:[]};
    state.prefs={schemaVersion:1};preferencesLoaded=true;state.fanDraft=dutyFirst(FanPresets.balanced);`);
  assert.equal(evaluate('fanPresetKey()'),'balanced');
  assert.notEqual(evaluate('JSON.stringify(state.fanDraft.radiatorCurve)'),evaluate('JSON.stringify(wireFanProfile(state.fanDraft).radiatorCurve)'));
  evaluate('state.fanDraft=wireFanProfile(state.fanDraft)');assert.equal(evaluate('fanPresetKey()'),'balanced');
  evaluate('state.fanDraft.radiatorPercent=73;state.fanDraft.casePercent=47');assert.equal(evaluate('fanPresetKey()'),'balanced','curve ignores inactive manual duties');
  evaluate('state.fanDraft.radiatorCurve[0].duty=64');assert.equal(evaluate('fanPresetKey()'),'custom');
  evaluate('state.fanDraft=dutyFirst(FanPresets.balanced);state.fanDraft.caseCurve[1].temperature=56');assert.equal(evaluate('fanPresetKey()'),'custom');
  evaluate('state.fanDraft=dutyFirst(FanPresets.balanced);state.fanDraft.caseCurve.pop()');assert.equal(evaluate('fanPresetKey()'),'custom');
  evaluate('state.fanDraft={...clone(FanPresets.balanced),mode:"manual"}');assert.equal(evaluate('fanPresetKey()'),'custom','mode must match');
  evaluate('state.fanDraft={...clone(FanPresets.silent),radiatorCurve:clone(FanPresets.balanced.radiatorCurve),caseCurve:clone(FanPresets.balanced.caseCurve)}');assert.equal(evaluate('fanPresetKey()'),'silent','manual ignores inactive curves');
  evaluate('state.fanDraft.radiatorPercent=73;state.fanDraft.casePercent=47');assert.equal(evaluate('fanPresetKey()'),'custom');
  evaluate('state.fanDraft=clone(FanPresets.silent);state.fanDraft.casePercent=26');assert.equal(evaluate('fanPresetKey()'),'custom');
  evaluate('state.fanDraft=clone(FanPresets.silent);state.fanDraft.radiatorPercent="40"');assert.equal(evaluate('fanPresetKey()'),'custom','duties must be numeric');
  assert.deepEqual(JSON.parse(evaluate('JSON.stringify(FanPresets.silent.radiatorCurve)')),[{temperature:40,duty:40},{temperature:55,duty:40},{temperature:65,duty:60},{temperature:80,duty:100}]);
  assert.deepEqual(JSON.parse(evaluate('JSON.stringify(FanPresets.silent.caseCurve)')),[{temperature:40,duty:25},{temperature:55,duty:30},{temperature:65,duty:55},{temperature:80,duty:100}]);
  assert.deepEqual(JSON.parse(evaluate('JSON.stringify(FanPresets.balanced.radiatorCurve)')),[{temperature:40,duty:65},{temperature:55,duty:65},{temperature:65,duty:80},{temperature:80,duty:100}]);
  assert.deepEqual(JSON.parse(evaluate('JSON.stringify(FanPresets.balanced.caseCurve)')),[{temperature:40,duty:55},{temperature:55,duty:65},{temperature:65,duty:80},{temperature:80,duty:100}]);

  // Exercise actual execute/persist/refresh functions against an in-memory RPC service.
  evaluate(`render=production.render;refresh=production.refresh;persist=production.persist;execute=production.execute;
    globalThis.fanCalls=[];globalThis.storedPrefs={schemaVersion:1};globalThis.serviceProfile=wireFanProfile(FanPresets.balanced);
    rpc=async(method,params)=>{fanCalls.push({method,params:clone(params)});if(method==='app.execute'){if(params.deviceId!=='fan'||params.capability!==Capabilities.FanProfile)throw new Error('Unexpected command');serviceProfile=wireFanProfile(params.parameters);return {status:'applied'}}if(method==='profiles.write'){storedPrefs=JSON.parse(JSON.stringify(params));return {}}if(method==='app.refresh'||method==='app.snapshot')return {version:'1.0',devices:[{...clone(selectedFan()),metadata:{profile:clone(serviceProfile),session:{status:'ready'}}}],telemetry:[],diagnostics:[]};throw new Error('Unexpected RPC '+method)};
    state.fanDraft=clone(FanPresets.balanced);render();
    document.querySelector('[data-fan-preset="silent"]').dispatch('click');`);
  assert.equal(evaluate('state.fanDraft.mode'),'manual');assert.equal(evaluate('state.fanDirty'),true);
  const doc=context.document;
  assert.equal(doc.querySelectorAll('[data-manual]').length,2);
  for(const selector of ['#curve-svg','#curve-wrap','.curve-numbers','[data-point-duty]','[data-point-temperature]','[data-target]'])assert.equal(doc.querySelector(selector),null,`manual must not render ${selector}`);
  assert.ok(evaluate('fanEditor(selectedFan())').includes('80°C 满速保护'));assert.ok(evaluate('fanPresets()').includes('固定最低 40% / 25% · 80°C 高温保护'));
  await evaluate('applyFan()');
  assert.equal(evaluate('state.errors.length'),0);assert.equal(evaluate('state.fanDirty'),false);assert.equal(evaluate('state.connected'),true);assert.equal(evaluate('fanPresetKey()'),'silent');
  assert.equal(evaluate('storedPrefs.fanProfiles.fan.mode'),'manual');assert.equal(evaluate('storedPrefs.fanProfiles.fan.radiatorPercent'),40);assert.equal(evaluate('storedPrefs.fanProfiles.fan.casePercent'),25);
  assert.deepEqual(JSON.parse(evaluate('JSON.stringify(fanCalls.map(c=>c.method))')),['app.execute','profiles.write','app.refresh']);
  assert.equal(doc.querySelector('[data-fan-preset="silent"]').attributes['aria-pressed'],'true');
  await evaluate('refresh(false)');assert.equal(evaluate('fanPresetKey()'),'silent');
  const manual=key=>doc.querySelector(`[data-manual="${key}"]`);
  manual('radiator').value='73';manual('radiator').dispatch('change');manual('case').value='47';manual('case').dispatch('change');
  assert.equal(evaluate('state.fanDirty'),true);assert.equal(evaluate('fanPresetKey()'),'custom');assert.equal(doc.querySelector('[data-fan-preset="custom"]').attributes['aria-pressed'],'true');assert.equal(doc.querySelector('[data-fan-preset="silent"]').classList.contains('active'),false);
  manual('radiator').value='40';manual('radiator').dispatch('change');manual('case').value='25';manual('case').dispatch('change');
  assert.equal(doc.querySelector('[data-fan-preset="silent"]').attributes['aria-pressed'],'true','numeric edits restore preset highlight');
  manual('radiator').value='73';manual('radiator').dispatch('change');
  const beforeDiscard=evaluate('fanCalls.length');doc.getElementById('discard').dispatch('click');
  assert.equal(evaluate('state.fanDirty'),false);assert.equal(evaluate('fanPresetKey()'),'silent');assert.equal(evaluate('state.fanDraft.radiatorPercent'),40);assert.equal(evaluate('fanCalls.length'),beforeDiscard);
  doc.querySelector('[data-fan-preset="custom"]').dispatch('click');
  assert.equal(evaluate('state.fanDraft.mode'),'curve');assert.equal(evaluate('state.fanDirty'),true);assert.equal(doc.querySelector('.curve-numbers').open,true);assert.equal(doc.activeElement,doc.querySelector('[data-point-duty]'));assert.ok(doc.getElementById('curve-svg'));
  assert.equal(doc.querySelectorAll('[data-manual]').length,0);assert.ok(evaluate('fanEditor(selectedFan())').includes('过滤短时温度尖峰，通过迟滞和平滑调节'));
  const duty=doc.querySelector('[data-point-duty="0"]');duty.value='41';duty.dispatch('change');
  // Silent's next node is 40%, so 41% must fail validation without corrupting the draft.
  assert.equal(evaluate('state.fanDraft.radiatorCurve[0].duty'),40);assert.equal(Number(duty.value),40);
  const secondDuty=doc.querySelector('[data-point-duty="1"]');secondDuty.value='45';secondDuty.dispatch('change');
  assert.equal(evaluate('state.fanDraft.radiatorCurve[1].duty'),45);assert.equal(doc.querySelector('[data-fan-preset="custom"]').attributes['aria-pressed'],'true');assert.ok(doc.getElementById('curve-path').attributes.d);
  const temperature=doc.querySelector('[data-point-temperature="1"]');temperature.value='56';temperature.dispatch('change');assert.equal(evaluate('state.fanDraft.radiatorCurve[1].temperature'),56);
  doc.getElementById('discard').dispatch('click');assert.equal(evaluate('fanPresetKey()'),'silent');assert.equal(doc.querySelector('.curve-numbers'),null);
  const mode=doc.getElementById('fan-mode');mode.value='curve';mode.dispatch('change');assert.ok(doc.querySelector('[data-point-duty]'));assert.equal(evaluate('state.fanDraft.mode'),'curve');
  doc.querySelector('[data-fan-preset="balanced"]').dispatch('click');const balancedDuty=doc.querySelector('[data-point-duty="0"]');balancedDuty.value='64';balancedDuty.dispatch('change');assert.equal(doc.querySelector('[data-fan-preset="custom"]').attributes['aria-pressed'],'true');balancedDuty.value='65';balancedDuty.dispatch('change');assert.equal(doc.querySelector('[data-fan-preset="balanced"]').attributes['aria-pressed'],'true');
  console.log('PASS: Wire-shaped field-order roundtrip, effective-field preset matching, silent apply/save/refresh, discard, manual preset highlights, manual hides curves, custom switches to editable curves, curve numeric validation and highlights.');
})().catch(error=>{console.error(error);process.exitCode=1});
