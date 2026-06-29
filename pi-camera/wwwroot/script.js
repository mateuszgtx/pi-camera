let state={}, options={}, saveTimer=null, currentMode='raw';

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
  for(const x of ['basic','photo','look','advanced','wifi']){
    $(x).classList.toggle('on',x===id);
    $('tab-'+x).classList.toggle('on',x===id);
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
  toast(state.captureKind==='GlitchPhoto' ? 'Robię glitch...' : 'Robię zdjęcie...');
  const r=await fetch('/api/capture',{method:'POST'});
  let msg='Błąd zdjęcia';
  try{
    const data=await r.json();
    if(r.ok){
      msg = data.captureKind==='GlitchPhoto' && Number(data.glitchPhotoCount||1)>1
        ? `Glitch x${data.glitchPhotoCount} zlecony`
        : 'Zdjęcie zlecone';
    }else{
      msg = data.message || msg;
    }
  }catch{}
  toast(msg);
}

async function toggleVideo(){
  const r=await fetch('/api/video/toggle',{method:'POST'});
  toast(r.ok?'Wideo przełączone':'Błąd wideo');
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
  box.innerHTML='<div class="mini">Ładuję...</div>';
  try{
    const r=await fetch('/api/photos?ts='+Date.now());
    const list=await r.json();
    if(!list.length){box.className='';box.innerHTML='<div class="mini">Brak zdjęć/filmów.</div>';return}
    box.className='photosGrid';
    box.innerHTML=list.map(p=>{
      const name=esc(p.name);
      const enc=encodeURIComponent(p.name);
      const isImg=/\.(jpg|jpeg|png|bmp)$/i.test(p.name);
      const preview=isImg
        ? `<button class="thumbBtn" onclick="openLightbox('${p.url}')"><img class="thumb" src="${p.url}" loading="lazy"></button>`
        : `<a class="thumbBtn" href="${p.url}" target="_blank"><div class="fileIcon">🎬</div></a>`;
      return `<div class="photo">${preview}<div class="photoInfo"><div class="photoName" title="${name}">${name}</div><div class="meta">${sizeText(p.size)}</div><div class="photoActions"><button class="ghost" onclick="window.open('${p.url}','_blank')">Otwórz</button><button class="danger" onclick="delPhoto('${enc}')">Usuń</button></div></div></div>`;
    }).join('');
  }catch(e){
    box.className='';
    box.innerHTML='<div class="mini">Nie udało się wczytać galerii.</div>';
  }
}

async function delPhoto(name){
  if(!confirm('Usunąć plik?')) return;
  const r=await fetch('/api/photos/'+name,{method:'DELETE'});
  toast(r.ok?'Usunięto':'Błąd usuwania');
  loadPhotos();
}


async function loadNetwork(){
  const status=$('networkStatusV');
  const list=$('savedWifiList');
  if(status) status.textContent='ładuję...';
  try{
    const data=await (await fetch('/api/network?ts='+Date.now())).json();
    if(status) status.textContent=`WiFi ${data.wifiRadio || '?'} • ${data.ip || 'brak IP'}`;
    if(list){
      const saved=data.saved||[];
      if(!saved.length){list.innerHTML='Brak zapisanych sieci.';return;}
      list.innerHTML=saved.map(n=>{
        const enc=encodeURIComponent(n);
        return `<div class="photoActions" style="margin:6px 0"><button class="ghost" onclick="connectWifi(decodeURIComponent('${enc}'))">Połącz</button><div style="align-self:center;color:#ddd;font-weight:800;overflow:hidden;text-overflow:ellipsis">${n}</div></div>`;
      }).join('');
    }
  }catch(e){
    if(status) status.textContent='błąd statusu';
    if(list) list.textContent='Nie udało się wczytać sieci.';
  }
}

async function saveWifiNetwork(connectNow){
  const ssid=($('wifiSsid')?.value||'').trim();
  const password=($('wifiPassword')?.value||'').trim();
  if(!ssid){toast('Wpisz SSID');return;}
  toast(connectNow?'Łączę WiFi...':'Zapisuję WiFi...');
  try{
    const r=await fetch('/api/network/wifi',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({ssid,password,connectNow})});
    const data=await r.json();
    toast(data.ok?'WiFi zapisane':(data.message||'Błąd WiFi'));
    loadNetwork();
  }catch(e){toast('Błąd WiFi')}
}

async function connectWifi(name){
  toast('Łączę '+name+'...');
  try{
    const r=await fetch('/api/network/wifi/connect',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name})});
    const data=await r.json();
    toast(data.ok?'Połączono WiFi':(data.message||'Błąd WiFi'));
    loadNetwork();
  }catch(e){toast('Błąd WiFi')}
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
  for(const k of ['captureKind','photoSource','photoFormat','videoFormat','sensorMode','paletteMode','photoWidth','photoHeight','jpgQuality','photoEv','videoSeconds','previewFps','randomFrameMinFps','randomFrameMaxFps','randomFrameSeconds','glitchStrength','glitchChangeMs','glitchPhotoCount','selectedColorAmount','redScale','greenScale','blueScale','lowSaveGamma','lowGrayYellowFix']) put(k,state[k]);
  const p=state.preview||{};
  for(const k of ['ev','sharpness','contrast','saturation','brightness','blackLevel','darkLevel','previewPixelSize','previewColorLevels','denoise']) put(k,p[k]);
}

async function save(){
  try{
    const r=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(state)});
    state=await r.json();
    sync();
    toast('Zapisano');
  }catch(e){toast('Błąd zapisu')}
}

function scheduleSave(){
  clearTimeout(saveTimer);
  saveTimer=setTimeout(save,180);
}

function set(k,val){
  state[k]=val;
  if(k==='photoSource' && val==='Preview'){
    state.preview=state.preview||{};
    if(Number(state.preview.previewPixelSize||1)>256) state.preview.previewPixelSize=256;
  }
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

document.addEventListener('keydown',e=>{if(e.key==='Escape'){closeLightbox();closeDrawers();}});
previewMode('raw');
loadSettings();
loadPhotos();
