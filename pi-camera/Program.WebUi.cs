using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using pi_camera.Services;

using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
namespace pi_camera;


public static partial class Program
{
    private static string ApiHomeHtml() => """
<!doctype html>
<html lang="pl">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover,user-scalable=no">
<title>Pi Camera</title>
<style>
:root{
  --bg:#050505;
  --panel:#101010;
  --panel2:#171717;
  --line:#272727;
  --text:#f7f7f7;
  --muted:#9a9a9a;
  --accent:#ffffff;
  --danger:#ff4d4d;
  --safe-top:env(safe-area-inset-top,0px);
  --safe-bottom:env(safe-area-inset-bottom,0px);
}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
html,body{margin:0;width:100%;height:100%;background:#000;color:var(--text);font-family:system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",Arial,sans-serif;overflow:hidden}
body{display:flex;flex-direction:column;touch-action:manipulation}
button,select,input{font:inherit}
button{appearance:none;border:0;border-radius:18px;background:var(--panel2);color:var(--text);font-weight:850;min-height:48px;padding:12px 14px}
button:active{transform:scale(.98);filter:brightness(1.25)}
button.primary{background:#fff;color:#000}
button.ghost{background:rgba(255,255,255,.08);border:1px solid rgba(255,255,255,.09)}
button.danger{background:#2b1010;color:#fff;border:1px solid #6b2929}
button.round{width:58px;height:58px;border-radius:50%;padding:0;font-size:22px}
.hidden{display:none!important}

/* preview */
#app{height:100%;display:grid;grid-template-rows:1fr auto;background:#000}
#previewWrap{
  position:relative;
  min-height:0;
  width:100vw;
  background:#000;
  padding-top:var(--safe-top);
  display:grid;
  grid-template-columns:1fr;
  gap:0;
}
.view{position:relative;min-width:0;min-height:0;background:#000;overflow:hidden}
.view img{width:100%;height:100%;object-fit:contain;display:block;background:#000}
#previewWrap.dual{gap:3px;background:#111}
#previewWrap.dual.landscape{grid-template-columns:1fr 1fr}
#previewWrap.dual.portrait{grid-template-rows:1fr 1fr}
.badge{
  position:absolute;
  top:calc(10px + var(--safe-top));
  left:10px;
  background:rgba(0,0,0,.55);
  border:1px solid rgba(255,255,255,.12);
  backdrop-filter:blur(8px);
  padding:7px 10px;
  border-radius:999px;
  font-size:12px;
  font-weight:850;
}
.status{
  position:absolute;
  top:calc(10px + var(--safe-top));
  right:10px;
  max-width:62vw;
  background:rgba(0,0,0,.62);
  border:1px solid rgba(255,255,255,.12);
  backdrop-filter:blur(8px);
  padding:8px 10px;
  border-radius:999px;
  font-size:12px;
  font-weight:700;
  opacity:0;
  transform:translateY(-6px);
  transition:.18s;
  pointer-events:none;
  white-space:nowrap;
  overflow:hidden;
  text-overflow:ellipsis;
}
.status.show{opacity:1;transform:translateY(0)}

/* top overlay mode buttons */
.modePills{
  position:absolute;
  left:50%;
  bottom:14px;
  transform:translateX(-50%);
  display:flex;
  gap:7px;
  padding:6px;
  background:rgba(0,0,0,.52);
  border:1px solid rgba(255,255,255,.12);
  border-radius:999px;
  backdrop-filter:blur(12px);
}
.modePills button{min-height:38px;border-radius:999px;padding:8px 13px;font-size:13px;background:transparent;color:#ddd}
.modePills button.on{background:#fff;color:#000}

/* bottom nav */
.bottom{
  padding:8px 10px calc(8px + var(--safe-bottom));
  background:linear-gradient(180deg,rgba(0,0,0,.82),#050505);
  border-top:1px solid #151515;
}
.actions{
  display:grid;
  grid-template-columns:1fr 1fr 1fr 1fr;
  gap:8px;
  max-width:720px;
  margin:0 auto;
}
.actions button{font-size:13px;min-width:0;padding:10px 8px;border-radius:18px}
.actions .capture{grid-column:span 1;background:#fff;color:#000;font-size:15px}

/* drawers */
.drawer{
  position:fixed;
  left:0;right:0;bottom:0;
  max-height:86vh;
  background:rgba(10,10,10,.98);
  border-top:1px solid #2b2b2b;
  border-radius:24px 24px 0 0;
  display:none;
  overflow:hidden;
  box-shadow:0 -20px 50px rgba(0,0,0,.85);
  z-index:20;
}
.drawer.open{display:flex;flex-direction:column}
.handle{width:48px;height:5px;border-radius:999px;background:#444;margin:10px auto 4px}
.drawerHead{
  display:flex;
  align-items:center;
  justify-content:space-between;
  gap:12px;
  padding:6px 14px 10px;
}
.drawerTitle{font-size:17px;font-weight:900}
.drawerBody{
  overflow:auto;
  padding:0 14px calc(18px + var(--safe-bottom));
  -webkit-overflow-scrolling:touch;
}
.closeBtn{min-height:38px;padding:8px 12px;border-radius:999px;background:#1d1d1d;color:#ddd}

/* settings */
.tabs{
  position:sticky;top:0;z-index:2;
  display:grid;
  grid-template-columns:repeat(4,1fr);
  gap:6px;
  background:rgba(10,10,10,.98);
  padding:6px 0 10px;
}
.tabs button{min-width:0;min-height:42px;padding:8px 5px;border-radius:14px;font-size:12px;background:#171717;color:#bbb}
.tabs button.on{background:#fff;color:#000}
.section{display:none}
.section.on{display:block}
.grid{display:grid;gap:10px}
.card{
  background:#141414;
  border:1px solid #242424;
  border-radius:18px;
  padding:12px;
}
.row{
  display:grid;
  grid-template-columns:1fr;
  gap:8px;
  padding:10px 0;
  border-bottom:1px solid #242424;
}
.row:last-child{border-bottom:0}
.rowTop{display:flex;justify-content:space-between;align-items:center;gap:10px}
label{font-size:14px;font-weight:760;color:#eee}
.val{font-size:13px;color:#9c9c9c;text-align:right;white-space:nowrap}
select,input[type=number],input[type=text]{
  width:100%;
  background:#0d0d0d;
  color:#fff;
  border:1px solid #303030;
  border-radius:14px;
  padding:12px;
  min-height:46px;
}
input[type=range]{
  width:100%;
  accent-color:#fff;
}
.mini{font-size:12px;color:#888;line-height:1.45;margin-top:8px}

/* gallery */
.galleryTools{display:grid;grid-template-columns:1fr auto;gap:8px;margin:4px 0 10px}
.photo{
  display:grid;
  grid-template-columns:64px 1fr auto;
  gap:10px;
  align-items:center;
  padding:10px 0;
  border-bottom:1px solid #222;
}
.thumb{
  width:64px;height:48px;border-radius:12px;background:#222;object-fit:cover;border:1px solid #333;
}
.photo a{color:#fff;text-decoration:none;font-weight:750;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.meta{font-size:12px;color:#888;margin-top:3px}
@media (max-width:360px){
  .actions{gap:6px}
  .actions button{font-size:12px;padding-left:5px;padding-right:5px}
  .modePills button{padding:7px 10px}
}
@media (max-width:520px){.tabs{overflow-x:auto;justify-content:flex-start}.row{padding:12px}.bottom button{font-size:12px;padding:10px 8px}.modePills{bottom:10px}.drawer{max-height:82vh}}
</style>
</head>
<body>
<div id="app">
  <div id="previewWrap">
    <div id="viewFiltered" class="view hidden">
      <img id="previewFiltered" alt="Podgląd z filtrem">
      <div class="badge">Filtr</div>
    </div>
    <div id="viewRaw" class="view">
      <img id="previewRaw" alt="Podgląd normalny">
      <div class="badge">Normal</div>
    </div>

    <div id="status" class="status"></div>

    <div class="modePills">
      <button id="modeRaw" class="on" onclick="previewMode('raw')">Normal</button>
      <button id="modeFiltered" onclick="previewMode('filtered')">Filtr</button>
      <button id="modeBoth" onclick="previewMode('both')">Oba</button>
    </div>
  </div>

  <div class="bottom">
    <div class="actions">
      <button class="capture" onclick="capture()">Zdjęcie</button>
      <button onclick="toggleVideo()">Wideo</button>
      <button onclick="openDrawer('settings')">Ustaw.</button>
      <button onclick="openDrawer('photos');loadPhotos()">Galeria</button>
    </div>
  </div>
</div>

<div id="photos" class="drawer">
  <div class="handle"></div>
  <div class="drawerHead">
    <div class="drawerTitle">Galeria</div>
    <button class="closeBtn" onclick="closeDrawers()">Zamknij</button>
  </div>
  <div class="drawerBody">
    <div class="galleryTools">
      <button class="ghost" onclick="loadPhotos()">Odśwież</button>
      <button class="ghost" onclick="openCurrent()">Otwórz panel</button>
    </div>
    <div id="photosList"></div>
  </div>
</div>

<div id="settings" class="drawer">
  <div class="handle"></div>
  <div class="drawerHead">
    <div class="drawerTitle">Ustawienia</div>
    <button class="closeBtn" onclick="closeDrawers()">Zamknij</button>
  </div>
  <div class="drawerBody">
    <div class="tabs">
      <button id="tab-basic" class="on" onclick="tab('basic')">Tryb</button>
      <button id="tab-photo" onclick="tab('photo')">Foto</button>
      <button id="tab-look" onclick="tab('look')">Kolor</button>
      <button id="tab-advanced" onclick="tab('advanced')">Live</button>
    </div>

    <div id="basic" class="section on">
      <div class="card grid">
        <div class="row"><div class="rowTop"><label>Tryb zapisu</label></div><select id="captureKind" onchange="set('captureKind',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Źródło zdjęcia</label></div><select id="photoSource" onchange="set('photoSource',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Tryb sensora</label></div><select id="sensorMode" onchange="set('sensorMode',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Format wideo</label></div><select id="videoFormat" onchange="set('videoFormat',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Czas wideo</label><span class="val"><span id="videoSecondsV"></span>s</span></div><input id="videoSeconds" type="range" min="0" max="300" step="1" oninput="setNum('videoSeconds',this.value)"></div>
      </div>
    </div>

    <div id="photo" class="section">
      <div class="card grid">
        <div class="row"><div class="rowTop"><label>Format zdjęcia</label></div><select id="photoFormat" onchange="set('photoFormat',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Szerokość</label><span class="val" id="photoWidthV"></span></div><input id="photoWidth" type="range" min="320" max="4056" step="16" oninput="setNum('photoWidth',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Wysokość</label><span class="val" id="photoHeightV"></span></div><input id="photoHeight" type="range" min="240" max="3040" step="16" oninput="setNum('photoHeight',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Jakość JPG</label><span class="val" id="jpgQualityV"></span></div><input id="jpgQuality" type="range" min="70" max="100" step="1" oninput="setNum('jpgQuality',this.value)"></div>
        <div class="row"><div class="rowTop"><label>EV zdjęcia</label><span class="val" id="photoEvV"></span></div><input id="photoEv" type="range" min="-8" max="8" step="0.1" oninput="setNum('photoEv',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Random min FPS</label><span class="val" id="randomFrameMinFpsV"></span></div><input id="randomFrameMinFps" type="range" min="1" max="30" step="1" oninput="setNum('randomFrameMinFps',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Random max FPS</label><span class="val" id="randomFrameMaxFpsV"></span></div><input id="randomFrameMaxFps" type="range" min="1" max="30" step="1" oninput="setNum('randomFrameMaxFps',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Random sekundy</label><span class="val" id="randomFrameSecondsV"></span></div><input id="randomFrameSeconds" type="range" min="1" max="120" step="1" oninput="setNum('randomFrameSeconds',this.value)"></div>
      </div>
    </div>

    <div id="look" class="section">
      <div class="card grid">
        <div class="row"><div class="rowTop"><label>Paleta</label></div><select id="paletteMode" onchange="set('paletteMode',this.value)"></select></div>
        <div class="row"><div class="rowTop"><label>Ilość kolorów</label><span class="val" id="selectedColorAmountV"></span></div><input id="selectedColorAmount" type="range" min="2" max="256" step="1" oninput="setNum('selectedColorAmount',this.value)"><div class="mini">Najlepiej używać wartości: 2, 4, 8, 16, 32, 64, 128, 256.</div></div>
        <div class="row"><div class="rowTop"><label>PIKSELE / JAKOŚĆ</label><span class="val" id="previewPixelSizeV2"></span></div><input id="pixelSizeLook" type="range" min="1" max="2048" step="1" oninput="setPreviewNum('previewPixelSize',this.value)"><div class="mini">1 = mocny pixel-art, 2048 = najlepsza jakość. Podgląd pokazuje przeskalowany wgląd.</div></div>
        <div class="row"><div class="rowTop"><label>Red scale</label><span class="val" id="redScaleV"></span></div><input id="redScale" type="range" min="0" max="2" step="0.01" oninput="setNum('redScale',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Green scale</label><span class="val" id="greenScaleV"></span></div><input id="greenScale" type="range" min="0" max="2" step="0.01" oninput="setNum('greenScale',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Blue scale</label><span class="val" id="blueScaleV"></span></div><input id="blueScale" type="range" min="0" max="2" step="0.01" oninput="setNum('blueScale',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Gamma zapisu</label><span class="val" id="lowSaveGammaV"></span></div><input id="lowSaveGamma" type="range" min="0.35" max="2.5" step="0.01" oninput="setNum('lowSaveGamma',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Yellow fix</label><span class="val" id="lowGrayYellowFixV"></span></div><input id="lowGrayYellowFix" type="range" min="0" max="80" step="1" oninput="setNum('lowGrayYellowFix',this.value)"></div>
      </div>
    </div>

    <div id="advanced" class="section">
      <div class="card grid">
        <div class="row"><div class="rowTop"><label>EV podglądu</label><span class="val" id="evV"></span></div><input id="ev" type="range" min="-8" max="8" step="0.1" oninput="setPreviewNum('ev',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Kontrast</label><span class="val" id="contrastV"></span></div><input id="contrast" type="range" min="0" max="32" step="0.05" oninput="setPreviewNum('contrast',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Saturacja</label><span class="val" id="saturationV"></span></div><input id="saturation" type="range" min="0" max="32" step="0.05" oninput="setPreviewNum('saturation',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Jasność</label><span class="val" id="brightnessV"></span></div><input id="brightness" type="range" min="-1" max="1" step="0.01" oninput="setPreviewNum('brightness',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Ostrość</label><span class="val" id="sharpnessV"></span></div><input id="sharpness" type="range" min="0" max="16" step="0.05" oninput="setPreviewNum('sharpness',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Black level</label><span class="val" id="blackLevelV"></span></div><input id="blackLevel" type="range" min="0" max="240" step="1" oninput="setPreviewNum('blackLevel',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Dark level</label><span class="val" id="darkLevelV"></span></div><input id="darkLevel" type="range" min="0.25" max="2" step="0.01" oninput="setPreviewNum('darkLevel',this.value)"></div>
        <div class="row"><div class="rowTop"><label>PIKSELE / JAKOŚĆ</label><span class="val" id="previewPixelSizeV"></span></div><input id="previewPixelSize" type="range" min="1" max="2048" step="1" oninput="setPreviewNum('previewPixelSize',this.value)"><div class="mini">1 = duże bloki, 2048 = najlepsza jakość. Podgląd jest skalowany proporcjonalnie.</div></div>
        <div class="row"><div class="rowTop"><label>Preview colors</label><span class="val" id="previewColorLevelsV"></span></div><input id="previewColorLevels" type="range" min="2" max="256" step="1" oninput="setPreviewNum('previewColorLevels',this.value)"></div>
        <div class="row"><div class="rowTop"><label>Denoise</label></div><select id="denoise" onchange="setPreview('denoise',this.value)"></select></div>
      </div>
    </div>
  </div>
</div>

<script>
let state={}, options={}, saveTimer=null, currentMode='raw';

const $=id=>document.getElementById(id);

function toast(t){
  const s=$('status');
  s.textContent=t;
  s.classList.add('show');
  clearTimeout(toast.t);
  toast.t=setTimeout(()=>s.classList.remove('show'),1500);
}

function closeDrawers(){
  $('photos').classList.remove('open');
  $('settings').classList.remove('open');
}

function openDrawer(id){
  closeDrawers();
  $(id).classList.add('open');
}

function openCurrent(){
  window.open(location.href,'_blank');
}

function tab(id){
  for(const x of ['basic','photo','look','advanced']){
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

  wrap.classList.toggle('dual',mode==='both');
  wrap.classList.toggle('portrait',mode==='both' && innerHeight>=innerWidth);
  wrap.classList.toggle('landscape',mode==='both' && innerWidth>innerHeight);

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
  toast('Robię zdjęcie...');
  const r=await fetch('/api/capture',{method:'POST'});
  toast(r.ok?'Zdjęcie zlecone':'Błąd zdjęcia');
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
  box.innerHTML='<div class="mini">Ładuję...</div>';
  try{
    const r=await fetch('/api/photos?ts='+Date.now());
    const list=await r.json();
    if(!list.length){box.innerHTML='<div class="mini">Brak zdjęć/filmów.</div>';return}
    box.innerHTML=list.map(p=>{
      const isImg=/\.(jpg|jpeg|png|bmp)$/i.test(p.name);
      const thumb=isImg?`<img class="thumb" src="${p.url}">`:`<div class="thumb"></div>`;
      return `<div class="photo">${thumb}<div><a href="${p.url}" target="_blank">${p.name}</a><div class="meta">${sizeText(p.size)}</div></div><button class="small danger" onclick="delPhoto('${encodeURIComponent(p.name)}')">Usuń</button></div>`;
    }).join('');
  }catch(e){
    box.innerHTML='<div class="mini">Nie udało się wczytać galerii.</div>';
  }
}

async function delPhoto(name){
  if(!confirm('Usunąć plik?')) return;
  const r=await fetch('/api/photos/'+name,{method:'DELETE'});
  toast(r.ok?'Usunięto':'Błąd usuwania');
  loadPhotos();
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
  for(const k of ['captureKind','photoSource','photoFormat','videoFormat','sensorMode','paletteMode','photoWidth','photoHeight','jpgQuality','photoEv','videoSeconds','previewFps','randomFrameMinFps','randomFrameMaxFps','randomFrameSeconds','selectedColorAmount','redScale','greenScale','blueScale','lowSaveGamma','lowGrayYellowFix']) put(k,state[k]);
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

document.addEventListener('keydown',e=>{if(e.key==='Escape')closeDrawers()});
previewMode('raw');
loadSettings();
loadPhotos();
</script>
</body>
</html>
""";

}
