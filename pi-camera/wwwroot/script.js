let state={}, options={}, saveTimer=null, currentMode='raw', currentTab='basic';
let bluetoothScanActive=false, bluetoothScanTimer=null, bluetoothActionBusy=false, audioAutoRefreshTimer=null;
let audioListenActive=false;
let audioListenAbort=null, audioListenContext=null, audioListenProcessor=null;
let audioListenQueue=[], audioListenQueuedSamples=0, audioListenSampleRate=48000;

const $=id=>document.getElementById(id);

function toast(t){
  const s=$('status');
  s.textContent=t;
  s.classList.add('show');
  clearTimeout(toast.t);
  toast.t=setTimeout(()=>s.classList.remove('show'),1500);
}

function refreshLayoutAfterDrawerChange(){
  requestAnimationFrame(()=>{
    previewMode(currentMode);
    setTimeout(()=>previewMode(currentMode),260);
  });
}

function closeDrawers(){
  $('photos').classList.remove('open');
  $('settings').classList.remove('open');
  document.body.classList.remove('settings-open','photos-open','drawer-open');
  refreshLayoutAfterDrawerChange();
}

function openDrawer(id){
  closeDrawers();
  $(id).classList.add('open');
  document.body.classList.add('drawer-open', id==='settings' ? 'settings-open' : 'photos-open');
  refreshLayoutAfterDrawerChange();
}

function openCurrent(){
  window.open(location.href,'_blank');
}

function esc(s){
  return String(s ?? '').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}

function openLightbox(url){
  $('lightboxImg').src=url;
  $('lightbox').classList.add('open');
}

function closeLightbox(){
  $('lightbox').classList.remove('open');
  $('lightboxImg').src='';
}

function tab(id){
  currentTab=id;
  for(const x of ['basic','stream','photo','look','advanced','wifi','audio']){
    $(x).classList.toggle('on',x===id);
    $('tab-'+x).classList.toggle('on',x===id);
  }
  updateAudioAutoRefresh();
  if(id==='audio') loadAudio();
}

function updateAudioAutoRefresh(){
  clearInterval(audioAutoRefreshTimer);
  audioAutoRefreshTimer=null;
  if(currentTab==='audio'){
    audioAutoRefreshTimer=setInterval(()=>loadAudio({quiet:true}),3000);
  }
}

function setImg(id,url){
  const img=$(id);
  const old=img.getAttribute('data-url');
  if(old===url) return;
  img.setAttribute('data-url',url);
  img.src=url;
}

function previewMode(mode){
  currentMode=mode;
  const wrap=$('previewWrap');
  const raw=$('viewRaw');
  const filtered=$('viewFiltered');

  $('modeRaw').classList.toggle('on',mode==='raw');
  $('modeFiltered').classList.toggle('on',mode==='filtered');
  $('modeBoth').classList.toggle('on',mode==='both');

  raw.classList.toggle('hidden',mode==='filtered');
  filtered.classList.toggle('hidden',mode==='raw');

  const w=wrap.clientWidth || innerWidth;
  const h=wrap.clientHeight || innerHeight;

  wrap.classList.toggle('dual',mode==='both');
  wrap.classList.toggle('portrait',mode==='both' && h>=w);
  wrap.classList.toggle('landscape',mode==='both' && w>h);

  if(mode==='raw'){
    setImg('previewRaw','/api/stream.mjpg?raw=true&q=42&fps=18&ts='+Date.now());
    setImg('previewFiltered','');
  }else if(mode==='filtered'){
    setImg('previewFiltered','/api/stream.mjpg?raw=false&q=45&fps=14&ts='+Date.now());
    setImg('previewRaw','');
  }else{
    setImg('previewRaw','/api/stream.mjpg?raw=true&q=40&fps=10&ts='+Date.now());
    setImg('previewFiltered','/api/stream.mjpg?raw=false&q=40&fps=10&ts='+Date.now());
  }
}
addEventListener('resize',()=>previewMode(currentMode));

async function capture(){
  toast(state.captureKind==='GlitchPhoto' ? 'Taking glitch...' : 'Taking photo...');
  const r=await fetch('/api/capture',{method:'POST'});
  let msg='Photo error';
  try{
    const data=await r.json();
    if(r.ok){
      msg = data.captureKind==='GlitchPhoto' && Number(data.glitchPhotoCount||1)>1
        ? `Glitch x${data.glitchPhotoCount} queued`
        : 'Photo queued';
    }else{
      msg = data.message || msg;
    }
  }catch{}
  toast(msg);
}

async function toggleVideo(){
  const r=await fetch('/api/video/toggle',{method:'POST'});
  toast(r.ok?'Video toggled':'Video error');
}

async function toggleStream(){
  const r=await fetch('/api/stream/toggle',{method:'POST'});
  let msg=r.ok?'Stream toggled':'Stream error';
  try{
    const data=await r.json();
    if(!r.ok) msg=data.message||msg;
    else {
      if(typeof data.streaming==='boolean') state.streaming=data.streaming;
      if(data.streamTarget) state.streamTarget=data.streamTarget;
      sync();
      if(data.requested==='toggle') msg=data.streaming?'Stream start':'Stream stop';
    }
  }catch{}
  toast(msg);
}

function actionLabel(){
  const mode=state.captureKind || 'Photo';
  if(mode==='Video') return state.recording && !state.randomRecording && !state.glitchVideoRecording ? 'Stop Video' : 'Video';
  if(mode==='RandomFrame') return state.recording && state.randomRecording ? 'Stop Random' : 'Random';
  if(mode==='GlitchVideo') return state.glitchVideoRecording ? 'Stop Glitch' : 'Glitch Video';
  if(mode==='Stream') return state.streaming ? 'Stop Stream' : 'Stream';
  if(mode==='GlitchPhoto') return 'Glitch Photo';
  return 'Photo';
}

function updateMainAction(){
  const btn=$('mainAction');
  if(btn) btn.textContent=actionLabel();
}

async function mainAction(){
  const mode=state.captureKind || 'Photo';
  toast(actionLabel()+'...');
  try{
    const r=await fetch('/api/action',{method:'POST'});
    const data=await r.json().catch(()=>({}));
    if(!r.ok){toast(data.message || 'Action error');return;}
    if(typeof data.streaming==='boolean') state.streaming=data.streaming;
    if(typeof data.recording==='boolean') state.recording=data.recording;
    if(typeof data.randomRecording==='boolean') state.randomRecording=data.randomRecording;
    if(typeof data.glitchVideoRecording==='boolean') state.glitchVideoRecording=data.glitchVideoRecording;
    sync();
    toast(data.message || (mode+' queued'));
    setTimeout(refreshState,900);
  }catch(e){toast('Action error')}
}

async function refreshState(){
  try{
    const data=await (await fetch('/api/settings')).json();
    state={...state,...data};
    sync();
  }catch{}
}

function sizeText(n){
  if(!n) return '';
  if(n>1024*1024) return (n/1024/1024).toFixed(1)+' MB';
  if(n>1024) return (n/1024).toFixed(0)+' KB';
  return n+' B';
}

async function loadPhotos(){
  const box=$('photosList');
  box.className='';
  box.innerHTML='<div class="mini">Loading...</div>';
  try{
    const r=await fetch('/api/photos?ts='+Date.now());
    const list=await r.json();
    if(!list.length){box.className='';box.innerHTML='<div class="mini">No photos or videos.</div>';return}
    box.className='photosGrid';
    box.innerHTML=list.map(p=>{
      const name=esc(p.name);
      const enc=encodeURIComponent(p.name);
      const isImg=/\.(jpg|jpeg|png|bmp)$/i.test(p.name);
      const preview=isImg
        ? `<button class="thumbBtn" onclick="openLightbox('${p.url}')"><img class="thumb" src="${p.url}" loading="lazy"></button>`
        : `<a class="thumbBtn" href="${p.url}" target="_blank"><div class="fileIcon">🎬</div></a>`;
      return `<div class="photo">${preview}<div class="photoInfo"><div class="photoName" title="${name}">${name}</div><div class="meta">${sizeText(p.size)}</div><div class="photoActions"><button class="ghost" onclick="window.open('${p.url}','_blank')">Open</button><button class="danger" onclick="delPhoto('${enc}')">Delete</button></div></div></div>`;
    }).join('');
  }catch(e){
    box.className='';
    box.innerHTML='<div class="mini">Could not load the gallery.</div>';
  }
}

async function delPhoto(name){
  if(!confirm('Delete this file?')) return;
  const r=await fetch('/api/photos/'+name,{method:'DELETE'});
  toast(r.ok?'Deleted':'Delete error');
  loadPhotos();
}

async function loadNetwork(){
  const status=$('networkStatusV');
  const list=$('savedWifiList');
  if(status) status.textContent='loading...';
  try{
    const data=await (await fetch('/api/network?ts='+Date.now())).json();
    if(status) status.textContent=`WiFi ${data.wifiRadio || '?'} • ${data.ip || 'no IP'}`;
    if(list){
      const saved=data.saved||[];
      if(!saved.length){list.innerHTML='No saved networks.';return;}
      list.innerHTML=saved.map(n=>{
        const enc=encodeURIComponent(n);
        return `<div class="photoActions" style="margin:6px 0"><button class="ghost" onclick="connectWifi(decodeURIComponent('${enc}'))">Connect</button><div style="align-self:center;color:#ddd;font-weight:800;overflow:hidden;text-overflow:ellipsis">${n}</div></div>`;
      }).join('');
    }
  }catch(e){
    if(status) status.textContent='status error';
    if(list) list.textContent='Could not load networks.';
  }
}

async function saveWifiNetwork(connectNow){
  const ssid=($('wifiSsid')?.value||'').trim();
  const password=($('wifiPassword')?.value||'').trim();
  if(!ssid){toast('Enter SSID');return;}
  toast(connectNow?'Connecting WiFi...':'Saving WiFi...');
  try{
    const r=await fetch('/api/network/wifi',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({ssid,password,connectNow})});
    const data=await r.json();
    toast(data.ok?'WiFi saved':(data.message||'WiFi error'));
    loadNetwork();
  }catch(e){toast('WiFi error')}
}

async function connectWifi(name){
  toast('Connecting '+name+'...');
  try{
    const r=await fetch('/api/network/wifi/connect',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name})});
    const data=await r.json();
    toast(data.ok?'WiFi connected':(data.message||'WiFi error'));
    loadNetwork();
  }catch(e){toast('WiFi error')}
}

function updateBluetoothScanButton(scanning, remaining){
  const btn=$('bluetoothScanBtn');
  if(!btn) return;
  btn.textContent=scanning ? `Cancel${remaining?` (${remaining}s)`:''}` : 'Scan';
  btn.classList.toggle('primary',!scanning);
  btn.classList.toggle('danger',!!scanning);
  btn.disabled=bluetoothActionBusy && !scanning;
}

function btResultMessage(data,fallback){
  return data?.bluetooth?.action?.message
    || data?.Bluetooth?.Action?.Message
    || data?.audio?.bluetooth?.action?.message
    || data?.audio?.Bluetooth?.Action?.Message
    || data?.message
    || data?.Message
    || fallback;
}

function setBluetoothScanPolling(on){
  clearInterval(bluetoothScanTimer);
  bluetoothScanTimer=null;
  if(on){
    bluetoothScanTimer=setInterval(()=>loadAudio({quiet:true}),1500);
  }
}

async function refreshBluetoothUntilStable(maxMs=12000){
  const started=Date.now();
  while(Date.now()-started<maxMs){
    await loadAudio({quiet:true});
    if(!bluetoothActionBusy && !bluetoothScanActive) break;
    await new Promise(r=>setTimeout(r,1000));
  }
  await loadAudio({quiet:true});
}

function setAudioListenState(active,message){
  audioListenActive=!!active;
  const btn=$('audioListenBtn');
  const val=$('audioListenV');
  const help=$('audioListenHelp');
  const player=$('audioMonitor');
  if(btn){
    btn.textContent=audioListenActive?'Stop':'Listen';
    btn.classList.toggle('danger',audioListenActive);
    btn.classList.toggle('primary',!audioListenActive);
  }
  if(val) val.textContent=audioListenActive?'On':'Off';
  if(help && message!==undefined) help.textContent=message || '';
  if(player) player.classList.add('hidden');
}

function resetAudioListenQueue(){
  audioListenQueue=[];
  audioListenQueuedSamples=0;
}

function pushAudioListenSamples(samples){
  if(!samples || !samples.length) return;
  audioListenQueue.push(samples);
  audioListenQueuedSamples+=samples.length;

  const target=Math.floor(audioListenSampleRate*0.12);
  const maximum=Math.floor(audioListenSampleRate*0.40);
  if(audioListenQueuedSamples<=maximum) return;

  let drop=audioListenQueuedSamples-target;
  while(drop>0 && audioListenQueue.length){
    const first=audioListenQueue[0];
    if(drop>=first.length){
      drop-=first.length;
      audioListenQueuedSamples-=first.length;
      audioListenQueue.shift();
    }else{
      audioListenQueue[0]=first.subarray(drop);
      audioListenQueuedSamples-=drop;
      drop=0;
    }
  }
}

function pullAudioListenSamples(output){
  output.fill(0);
  let pos=0;
  while(pos<output.length && audioListenQueue.length){
    const first=audioListenQueue[0];
    const take=Math.min(output.length-pos,first.length);
    output.set(first.subarray(0,take),pos);
    pos+=take;
    audioListenQueuedSamples-=take;
    if(take===first.length) audioListenQueue.shift();
    else audioListenQueue[0]=first.subarray(take);
  }
}

function cleanupAudioListenGraph(){
  if(audioListenAbort){
    try{audioListenAbort.abort();}catch{}
    audioListenAbort=null;
  }
  if(audioListenProcessor){
    try{audioListenProcessor.disconnect();}catch{}
    audioListenProcessor.onaudioprocess=null;
    audioListenProcessor=null;
  }
  if(audioListenContext){
    const ctx=audioListenContext;
    audioListenContext=null;
    try{ctx.close();}catch{}
  }
  resetAudioListenQueue();
}

function stopAudioListen(silent=false){
  cleanupAudioListenGraph();
  const player=$('audioMonitor');
  if(player){
    try{player.pause();}catch{}
    player.removeAttribute('src');
    try{player.load();}catch{}
    player.classList.add('hidden');
  }
  setAudioListenState(false,silent?'':'Listen stopped.');
}

function pcm16ChunkToFloat32(chunk,remainder){
  let bytes=chunk;
  if(remainder && remainder.length){
    bytes=new Uint8Array(remainder.length+chunk.length);
    bytes.set(remainder,0);
    bytes.set(chunk,remainder.length);
  }
  const usable=bytes.length-(bytes.length%2);
  const samples=new Float32Array(usable/2);
  const view=new DataView(bytes.buffer,bytes.byteOffset,usable);
  for(let i=0;i<samples.length;i++) samples[i]=Math.max(-1,Math.min(1,view.getInt16(i*2,true)/32768));
  const rest=usable<bytes.length ? bytes.slice(usable) : new Uint8Array(0);
  return {samples,remainder:rest};
}

async function startLowLatencyAudioListen(){
  const AC=window.AudioContext||window.webkitAudioContext;
  if(!AC || !window.ReadableStream) throw new Error('Low latency audio is not supported by this browser.');

  cleanupAudioListenGraph();
  audioListenAbort=new AbortController();
  audioListenContext=new AC({latencyHint:'interactive'});
  await audioListenContext.resume();
  audioListenSampleRate=Math.round(audioListenContext.sampleRate || Number(state.audioSampleRate) || 48000);

  audioListenProcessor=audioListenContext.createScriptProcessor(1024,0,1);
  audioListenProcessor.onaudioprocess=e=>pullAudioListenSamples(e.outputBuffer.getChannelData(0));
  audioListenProcessor.connect(audioListenContext.destination);

  setAudioListenState(true,'Opening low-latency audio monitor...');
  const url=`/api/audio/listen.raw?rate=${encodeURIComponent(audioListenSampleRate)}&ts=${Date.now()}`;

  (async()=>{
    let remainder=new Uint8Array(0);
    try{
      const response=await fetch(url,{signal:audioListenAbort.signal,cache:'no-store'});
      if(!response.ok || !response.body) throw new Error('Audio stream unavailable');
      setAudioListenState(true,'Low-latency monitor is using the active audio input.');
      const reader=response.body.getReader();
      while(audioListenActive){
        const {done,value}=await reader.read();
        if(done) break;
        const converted=pcm16ChunkToFloat32(value,remainder);
        remainder=converted.remainder;
        pushAudioListenSamples(converted.samples);
      }
      if(audioListenActive) setAudioListenState(false,'Audio monitor ended.');
    }catch(e){
      if(audioListenActive && e.name!=='AbortError'){
        stopAudioListen(true);
        setAudioListenState(false,'Audio monitor stopped. Check the selected input and press Listen again.');
        toast('Audio monitor stopped');
      }
    }
  })();
}

async function toggleAudioListen(){
  if(audioListenActive) return stopAudioListen();

  const btn=$('audioListenBtn');
  if(btn && btn.disabled) return toast('No audio input');

  try{
    await startLowLatencyAudioListen();
    toast('Audio monitor on');
  }catch(e){
    stopAudioListen(true);
    setAudioListenState(false,'Could not start low-latency audio monitor.');
    toast('Audio monitor error');
  }
}

function wireAudioMonitorEvents(){
  const player=$('audioMonitor');
  if(!player || player.dataset.wired) return;
  player.dataset.wired='1';
  player.addEventListener('ended',()=>setAudioListenState(false,'Audio monitor ended.'));
  player.addEventListener('error',()=>{
    if(audioListenActive){
      setAudioListenState(false,'Audio monitor stopped. Check the selected input and press Listen again.');
      toast('Audio monitor stopped');
    }
  });
}

async function loadAudio(opts={}){
  const status=$('audioStatusV');
  const btStatus=$('bluetoothStatusV');
  const btPower=$('bluetoothPowerV');
  const btAction=$('bluetoothActionV');
  const list=$('bluetoothList');
  const help=$('audioHelp');
  const sourcesBox=$('audioSources');
  const quiet=!!opts.quiet;
  if(status && !quiet) status.textContent='loading...';
  if(btPower && !quiet) btPower.textContent='loading...';
  if(list && !quiet && !bluetoothScanActive) list.innerHTML='Loading...';
  try{
    const data=await (await fetch('/api/audio?ts='+Date.now())).json();
    const active=data.active;
    const message=data.message || data.Message || '';
    if(status) status.textContent=active ? `${active.kind || 'audio'} • ${active.label || active.device}` : 'No input';
    if(help) help.textContent=message || '';

    wireAudioMonitorEvents();
    const listenBtn=$('audioListenBtn');
    const listenHelp=$('audioListenHelp');
    if(listenBtn && !audioListenActive) listenBtn.disabled=!active;
    if(listenHelp && !audioListenActive) listenHelp.textContent=active ? `Ready: ${active.label || active.device}` : 'No input to listen to.';

    if(Array.isArray(data.sources) && data.sources.length){
      const manual=$('audioDevice');
      if(manual && !manual.value && state.audioInputMode==='Manual') manual.placeholder=data.sources[0].device || manual.placeholder;
      if(sourcesBox){
        sourcesBox.innerHTML=data.sources.map(src=>{
          const rawFormat=src.format || src.Format || '';
          const rawDevice=src.device || src.Device || '';
          const kind=esc(src.kind || src.Kind || 'audio');
          const format=esc(rawFormat);
          const device=esc(rawDevice);
          const label=esc(src.label || src.Label || rawDevice);
          return `<div class="sourceRow"><div><b>${label}</b><br><span class="deviceMeta">${kind} • ${format} • ${device}</span></div><button class="ghost" onclick='useAudioSource(${JSON.stringify(rawFormat)},${JSON.stringify(rawDevice)})'>Use</button></div>`;
        }).join('');
      }
    }else if(sourcesBox){
      sourcesBox.textContent='No input sources detected.';
    }

    const bt=data.bluetooth || {};
    const powered=!!(bt.powered ?? bt.Powered);
    const scanning=!!(bt.scanning ?? bt.Scanning);
    const remaining=Number(bt.scanRemainingSeconds ?? bt.ScanRemainingSeconds ?? 0);
    const action=bt.action || bt.Action || {};
    const actionBusy=!!(action.busy ?? action.Busy);
    const actionMsg=action.message || action.Message || '';
    const actionOk=(action.ok ?? action.Ok ?? true)!==false;
    bluetoothActionBusy=actionBusy;
    bluetoothScanActive=scanning;
    updateBluetoothScanButton(scanning,remaining);
    setBluetoothScanPolling(scanning || actionBusy);
    if(btPower){
      btPower.textContent=powered ? 'ON' : 'OFF';
      btPower.classList.toggle('powerOn',powered);
      btPower.classList.toggle('powerOff',!powered);
    }
    $('bluetoothOnBtn') && ($('bluetoothOnBtn').disabled=actionBusy || powered);
    $('bluetoothOffBtn') && ($('bluetoothOffBtn').disabled=actionBusy || !powered);
    if(btAction){
      btAction.textContent=actionMsg || (powered ? 'Bluetooth is ON.' : 'Bluetooth is OFF.');
      btAction.classList.toggle('bad',!actionOk);
      btAction.classList.toggle('busy',actionBusy);
    }

    const showUnnamed=($('showUnnamedBluetooth')?.value==='true');
    const devices=showUnnamed ? ((bt.allDevices) || (bt.AllDevices) || []) : ((bt.devices) || (bt.Devices) || []);
    const hidden=Number(bt.hiddenCount ?? bt.HiddenCount ?? 0);
    const scanLog=bt.lastScanLog || bt.LastScanLog || '';
    if(btStatus){
      if(actionBusy) btStatus.textContent=actionMsg || 'Bluetooth busy...';
      else if(!powered) btStatus.textContent='Bluetooth OFF';
      else if(scanning) btStatus.textContent=`Scanning${remaining?` • ${remaining}s left`:''} • ${devices.length} found${(!showUnnamed && hidden)?` • ${hidden} hidden`:''}`;
      else if(devices.length) btStatus.textContent=`${devices.length} found${(!showUnnamed && hidden)?` • ${hidden} hidden`:''}`;
      else btStatus.textContent=hidden ? `${hidden} unnamed hidden` : 'No devices';
    }
    if(list){
      if(!powered){list.innerHTML='<b>Bluetooth is OFF.</b> Press On before scanning or connecting.';return;}
      if(!devices.length){list.innerHTML=scanning ? 'Scanning... put headphones into pairing mode. You can cancel scan anytime.' : ((hidden && !showUnnamed) ? 'Only unnamed devices found. Turn on Show unnamed or put headphones into pairing mode and press Scan.' : 'No Bluetooth devices yet. Press Scan.');return;}
      list.innerHTML=devices.map(d=>{
        const mac=esc(d.mac || d.Mac || '');
        const rawName=d.displayName || d.DisplayName || d.name || d.Name || `Unknown BT ${mac.slice(-5)}`;
        const name=esc(rawName);
        const connected=!!(d.connected ?? d.Connected);
        const paired=!!(d.paired ?? d.Paired);
        const hasName=!!(d.hasName ?? d.HasName);
        const audio=!!(d.isLikelyAudio ?? d.IsLikelyAudio);
        const meta=[connected?'connected':null,paired?'paired':'not paired',audio?'audio':null,!hasName?'unnamed':null].filter(Boolean).join(' • ');
        const disabled=actionBusy ? ' disabled' : '';
        const forgetBtn=paired ? `<button class="ghost" onclick="removeBluetooth('${mac}')"${disabled}>Forget</button>` : '';
        const connectBtn=connected
          ? `<button class="ghost" onclick="disconnectBluetooth('${mac}')"${disabled}>Disconnect</button>`
          : `<button class="primary" onclick="${paired?'connectBluetooth':'pairBluetooth'}('${mac}')"${disabled}>${paired?'Connect':'Pair'}</button>${forgetBtn}`;
        return `<div class="deviceRow"><div><div class="deviceName" title="${name}">${name}</div><div class="deviceMeta">${mac} • ${meta}</div></div><div class="deviceActions">${connectBtn}</div></div>`;
      }).join('') + (scanLog ? `<div class="deviceMeta scanLog">${esc(scanLog)}</div>` : '');
    }
  }catch(e){
    if(status) status.textContent='Audio error';
    if(list) list.textContent='Could not load audio devices.';
  }
}

async function useAudioSource(format,device){
  if(audioListenActive) stopAudioListen(true);
  await set('audioInputMode','Manual');
  await set('audioInputFormat',format || 'auto');
  await set('audioDevice',device || '');
  toast('Audio input selected');
  await loadSettings();
  await loadAudio();
}

async function setBluetoothPower(on){
  if(bluetoothActionBusy) return toast('Bluetooth is busy...');
  toast(on?'Turning Bluetooth on...':'Turning Bluetooth off...');
  bluetoothActionBusy=true;
  updateBluetoothScanButton(bluetoothScanActive,0);
  try{
    const r=await fetch('/api/audio/bluetooth/power',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled:!!on})});
    const data=await r.json();
    toast(btResultMessage(data,r.ok?(on?'Bluetooth on':'Bluetooth off'):'Bluetooth power error'));
    await refreshBluetoothUntilStable();
  }catch(e){toast('Bluetooth power error'); bluetoothActionBusy=false; await loadAudio({quiet:true})}
}

async function toggleBluetoothScan(){
  if(bluetoothScanActive) return cancelBluetoothScan();
  return scanBluetooth();
}

async function scanBluetooth(seconds=120){
  if(bluetoothActionBusy) return toast('Bluetooth is busy...');
  toast('Scanning Bluetooth...');
  try{
    const r=await fetch('/api/audio/bluetooth/scan',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({seconds})});
    const data=await r.json();
    bluetoothScanActive=!!r.ok;
    updateBluetoothScanButton(bluetoothScanActive,seconds);
    setBluetoothScanPolling(bluetoothScanActive);
    toast(r.ok?'Bluetooth scan started':(data.message||'Bluetooth scan error'));
    await loadAudio({quiet:true});
  }catch(e){toast('Bluetooth scan error')}
}

async function cancelBluetoothScan(silent=false){
  if(!silent) toast('Cancelling Bluetooth scan...');
  try{
    const r=await fetch('/api/audio/bluetooth/cancel',{method:'POST'});
    const data=await r.json();
    bluetoothScanActive=false;
    updateBluetoothScanButton(false,0);
    setBluetoothScanPolling(false);
    if(!silent) toast(r.ok?'Bluetooth scan cancelled':(data.message||'Bluetooth cancel error'));
    await loadAudio({quiet:true});
  }catch(e){if(!silent) toast('Bluetooth cancel error')}
}

async function pairBluetooth(mac){
  if(bluetoothActionBusy) return toast('Bluetooth is busy...');
  if(bluetoothScanActive) await cancelBluetoothScan(true);
  toast('Pairing Bluetooth...');
  bluetoothActionBusy=true;
  try{
    const r=await fetch('/api/audio/bluetooth/pair',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({mac})});
    const data=await r.json();
    toast(btResultMessage(data,r.ok?'Bluetooth paired and connected':'Bluetooth pair error'));
    await refreshBluetoothUntilStable();
  }catch(e){toast('Bluetooth pair error'); bluetoothActionBusy=false; await loadAudio({quiet:true})}
}

async function connectBluetooth(mac){
  if(bluetoothActionBusy) return toast('Bluetooth is busy...');
  if(bluetoothScanActive) await cancelBluetoothScan(true);
  toast('Connecting Bluetooth...');
  bluetoothActionBusy=true;
  try{
    const r=await fetch('/api/audio/bluetooth/connect',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({mac})});
    const data=await r.json();
    toast(btResultMessage(data,r.ok?'Bluetooth connected':'Bluetooth connect error'));
    await refreshBluetoothUntilStable();
  }catch(e){toast('Bluetooth connect error'); bluetoothActionBusy=false; await loadAudio({quiet:true})}
}

async function disconnectBluetooth(mac){
  if(bluetoothActionBusy) return toast('Bluetooth is busy...');
  if(bluetoothScanActive) await cancelBluetoothScan(true);
  toast('Disconnecting Bluetooth...');
  bluetoothActionBusy=true;
  try{
    const r=await fetch('/api/audio/bluetooth/disconnect',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({mac})});
    const data=await r.json();
    toast(btResultMessage(data,r.ok?'Bluetooth disconnected':'Bluetooth disconnect error'));
    await refreshBluetoothUntilStable();
  }catch(e){toast('Bluetooth disconnect error'); bluetoothActionBusy=false; await loadAudio({quiet:true})}
}

async function removeBluetooth(mac){
  if(bluetoothActionBusy) return toast('Bluetooth is busy...');
  if(bluetoothScanActive) await cancelBluetoothScan(true);
  toast('Forgetting Bluetooth device...');
  bluetoothActionBusy=true;
  try{
    const r=await fetch('/api/audio/bluetooth/remove',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({mac})});
    const data=await r.json();
    toast(btResultMessage(data,r.ok?'Bluetooth device forgotten':'Bluetooth forget error'));
    await refreshBluetoothUntilStable();
  }catch(e){toast('Bluetooth forget error'); bluetoothActionBusy=false; await loadAudio({quiet:true})}
}

function fillSelect(id,arr){
  const el=$(id);
  if(!el) return;
  el.innerHTML=(arr||[]).map(x=>`<option value="${x}">${x}</option>`).join('');
}

async function loadSettings(){
  options=await (await fetch('/api/settings/options')).json();
  state=await (await fetch('/api/settings')).json();

  fillSelect('captureKind',options.captureKinds);
  fillSelect('photoSource',options.photoSources);
  fillSelect('photoFormat',options.photoFormats);
  fillSelect('videoFormat',options.videoFormats);
  fillSelect('streamOutputFormat',options.streamOutputFormats);
  fillSelect('audioInputMode',options.audioInputModes);
  fillSelect('audioInputFormat',options.audioInputFormats);
  fillSelect('sensorMode',options.sensorModes);
  fillSelect('paletteMode',options.paletteModes);
  fillSelect('glitchPhotoCount',options.glitchPhotoCountChoices || [1,2,3,4,5,6,8,10,12]);
  fillSelect('denoise',options.denoise);

  sync();
}

function valText(v){
  if(typeof v==='number'){
    if(Math.abs(v-Math.round(v))<0.001) return String(Math.round(v));
    return v.toFixed(2).replace(/0+$/,'').replace(/\.$/,'');
  }
  return v ?? '';
}

function put(id,value){
  const el=$(id);
  if(el){
    if(el.tagName==='SELECT') el.value=value;
    else el.value=value;
  }
  const v=$(id+'V');
  if(v) v.textContent=valText(value);
  if(id==='previewPixelSize'){
    const p2=$('pixelSizeLook'); if(p2) p2.value=value;
    const v2=$('previewPixelSizeV2'); if(v2) v2.textContent=valText(value);
  }
}

function sync(){
  for(const k of ['captureKind','photoSource','photoFormat','videoFormat','streamUrl','streamOutputFormat','streamFps','streamBitrateKbps','streamJpegQuality','streamUseRaw','audioEnabled','audioInputMode','audioInputFormat','audioDevice','audioSampleRate','audioBitrateKbps','sensorMode','paletteMode','photoWidth','photoHeight','jpgQuality','photoEv','videoSeconds','previewFps','randomFrameMinFps','randomFrameMaxFps','randomFrameSeconds','glitchStrength','glitchChangeMs','glitchPhotoCount','selectedColorAmount','redScale','greenScale','blueScale','lowSaveGamma','lowGrayYellowFix']) put(k,state[k]);
  const st=$('streamTargetV'); if(st) st.textContent=state.streaming?'STREAM ON':(state.streamTarget||'');
  updateMainAction();
  const p=state.preview||{};
  for(const k of ['ev','sharpness','contrast','saturation','brightness','blackLevel','darkLevel','previewPixelSize','previewColorLevels','denoise']) put(k,p[k]);
}

async function save(){
  try{
    const r=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(state)});
    state=await r.json();
    sync();
    toast('Saved');
  }catch(e){toast('Save error')}
}

function scheduleSave(){
  clearTimeout(saveTimer);
  saveTimer=setTimeout(save,180);
}

function set(k,val){
  if(audioListenActive && ['audioInputMode','audioInputFormat','audioDevice'].includes(k)) stopAudioListen(true);
  state[k]=val;
  if(k==='captureKind') updateMainAction();
  if(k==='photoSource' && val==='Preview'){
    state.preview=state.preview||{};
    if(Number(state.preview.previewPixelSize||1)>256) state.preview.previewPixelSize=256;
  }
  scheduleSave();
}

function setBool(k,val){
  if(audioListenActive && k==='audioEnabled') stopAudioListen(true);
  state[k]=String(val)==='true';
  put(k,state[k]);
  scheduleSave();
}

function setNum(k,val){
  state[k]=Number(val);
  if(k==='selectedColorAmount'){
    state.preview=state.preview||{};
    state.preview.previewColorLevels=state[k];
    put('previewColorLevels',state[k]);
  }
  put(k,state[k]);
  scheduleSave();
}

function setPreview(k,val){
  state.preview=state.preview||{};
  state.preview[k]=val;
  scheduleSave();
}

function setPreviewNum(k,val){
  state.preview=state.preview||{};
  if(k==='previewPixelSize' && state.photoSource==='Preview') val=Math.min(Number(val),256);
  state.preview[k]=Number(val);
  if(k==='previewColorLevels'){
    state.selectedColorAmount=state.preview[k];
    put('selectedColorAmount',state.selectedColorAmount);
  }
  put(k,state.preview[k]);
  scheduleSave();
}

async function resetSettings(){
  if(!confirm('Reset settings to defaults?')) return;
  try{
    const r=await fetch('/api/settings/reset',{method:'POST'});
    state=await r.json();
    sync();
    toast(r.ok?'Defaults restored':'Reset error');
  }catch(e){toast('Reset error')}
}

document.addEventListener('keydown',e=>{if(e.key==='Escape'){closeLightbox();closeDrawers();}});
wireAudioMonitorEvents();
previewMode('raw');
loadSettings();
loadPhotos();
