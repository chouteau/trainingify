window.superClimbMap = (() => {
  let map, marker;
  function initialize(id, points) {
    dispose(); const el=document.getElementById(id); if(!el || !window.L || !points.length) return;
    el.innerHTML=''; map=L.map(el,{zoomControl:true});
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:19,attribution:'&copy; OpenStreetMap contributors',errorTileUrl:''}).addTo(map);
    const latlngs=points.map(p=>[p.lat,p.lon]); L.polyline(latlngs,{color:'#24d47b',weight:5,opacity:.95}).addTo(map);
    L.circleMarker(latlngs[0],{radius:6,color:'#fff',fillColor:'#24d47b',fillOpacity:1}).addTo(map);
    L.circleMarker(latlngs.at(-1),{radius:7,color:'#fff',fillColor:'#ff7a45',fillOpacity:1}).addTo(map);
    marker=L.circleMarker(latlngs[0],{radius:9,color:'#fff',weight:3,fillColor:'#1db7ff',fillOpacity:1}).addTo(map);
    map.fitBounds(L.latLngBounds(latlngs),{padding:[24,24]}); setTimeout(()=>map?.invalidateSize(),50);
  }
  function setPosition(lat,lon){ marker?.setLatLng([lat,lon]); }
  function dispose(){ if(map){map.remove();map=null;marker=null;} }
  return {initialize,setPosition,dispose};
})();
