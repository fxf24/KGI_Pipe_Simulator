// iPhone (AR Quick Look) image-anchored placement, experimental.
// A printed, richly textured image per line is the anchor: Quick Look finds it on the floor and puts the
// model's first reference point (refPoints[0]) on the image center, with the pipe direction along the
// arrow (image right). Uses Apple's preliminary USD anchoring schema (Preliminary_AnchoringAPI, type "image").
import * as THREE from 'three';
import { USDZExporter } from 'three/addons/exporters/USDZExporter.js';
import { unzipSync, zipSync, strToU8, strFromU8 } from 'three/addons/libs/fflate.module.js';

export const IOS_MARKER_MM = 180;   // printed width = height of the image (markers.html)
const PX = 1024;

function rng(str) {   // deterministic per line, so the print and the phone draw the same image
  let h = 1779033703 ^ str.length;
  for (let i = 0; i < str.length; i++) { h = Math.imul(h ^ str.charCodeAt(i), 3432918353); h = (h << 13) | (h >>> 19); }
  return () => {
    h = Math.imul(h ^ (h >>> 16), 2246822507); h = Math.imul(h ^ (h >>> 13), 3266489909);
    return ((h ^= h >>> 16) >>> 0) / 4294967296;
  };
}

// Irregular, high-contrast, non-repeating pattern (what ARKit image detection needs). No text: fonts differ per device.
export function drawIosMarker(canvas, lineId) {
  canvas.width = canvas.height = PX;
  const g = canvas.getContext('2d'), r = rng('kgi-ios-' + lineId);
  g.fillStyle = '#fff'; g.fillRect(0, 0, PX, PX);
  const gray = () => { const v = Math.floor(r() * 170); return `rgb(${v},${v},${v})`; };
  for (let i = 0; i < 170; i++) {
    const x = r() * PX, y = r() * PX, s = 20 + Math.pow(r(), 2) * 220, k = r();
    g.fillStyle = gray(); g.strokeStyle = gray(); g.lineWidth = 6 + r() * 18;
    g.save(); g.translate(x, y); g.rotate(r() * Math.PI * 2);
    if (k < 0.3) g.fillRect(-s / 2, -s / 3, s, s * (0.2 + r() * 0.6));
    else if (k < 0.55) { g.beginPath(); g.moveTo(0, -s / 2); g.lineTo(s / 2, s * (r() - 0.2)); g.lineTo(-s / 2, s / 2); g.closePath(); g.fill(); }
    else if (k < 0.75) { g.beginPath(); g.arc(0, 0, s / 2, 0, Math.PI * (0.8 + r() * 1.2)); g.stroke(); }
    else if (k < 0.9) { g.beginPath(); g.moveTo(-s / 2, 0); g.lineTo(s / 2, r() * s / 2); g.stroke(); }
    else { g.fillStyle = '#fff'; g.beginPath(); g.arc(0, 0, s / 3, 0, Math.PI * 2); g.fill(); }
    g.restore();
  }
  g.strokeStyle = '#000'; g.lineWidth = 24; g.strokeRect(12, 12, PX - 24, PX - 24);
  return canvas;
}

const pngBytes = canvas => new Promise(res => canvas.toBlob(b => b.arrayBuffer().then(a => res(new Uint8Array(a))), 'image/png'));

// USDZ needs every file 64-byte aligned inside the zip (same trick as three.js USDZExporter).
function zipAligned(files) {
  let offset = 0;
  for (const name in files) {
    const file = files[name];
    offset += 34 + name.length;
    const mod = offset & 63;
    if (mod !== 4) files[name] = [file, { extra: { 12345: new Uint8Array(64 - mod) } }];
    offset = file.length;
  }
  return zipSync(files, { level: 0 });
}

// model: three.js object in site coordinates (x = E, y = EL, z = -N, meters), meta: models/<id>.json.
// widthUnit: physicalWidth value scale (Apple docs say centimeters; 'm' is a fallback to test).
export async function buildAnchoredUSDZ(model, meta, { lowered = false, widthUnit = 'cm' } = {}) {
  const at = meta.refPoints[0].at, dir = (meta.markers && meta.markers[0] && meta.markers[0].dir) || [1, 0];
  const ref0 = new THREE.Vector3(at[0] / 1000, at[2] / 1000, -at[1] / 1000);
  const box = new THREE.Box3().setFromObject(model);
  const inner = new THREE.Group(); inner.add(model.clone());
  inner.position.set(-ref0.x, lowered ? -box.min.y : -ref0.y, -ref0.z);
  const outer = new THREE.Group(); outer.add(inner);
  outer.rotation.y = -Math.atan2(dir[1], dir[0]);   // pipe direction (E, N) -> anchor +X (image right)
  const scene = new THREE.Scene(); scene.add(outer); scene.updateMatrixWorld(true);

  const files = unzipSync(await new USDZExporter().parse(scene, { quickLookCompatible: true }));
  const width = widthUnit === 'm' ? IOS_MARKER_MM / 1000 : IOS_MARKER_MM / 10;
  let usda = strFromU8(files['model.usda']);
  usda = usda.replace('def Xform "Scene" (', 'def Xform "Scene" (\n\t\t\tprepend apiSchemas = ["Preliminary_AnchoringAPI"]');
  usda = usda.replace(/token preliminary:anchoring:type = "plane"\s*token preliminary:planeAnchoring:alignment = "horizontal"/,
`uniform token preliminary:anchoring:type = "image"
		rel preliminary:imageAnchoring:referenceImage = </Root/Scenes/Scene/KgiMarker>

		def Preliminary_ReferenceImage "KgiMarker"
		{
			uniform asset image = @marker.png@
			uniform double physicalWidth = ${width}
		}
`);
  if (!usda.includes('imageAnchoring')) throw new Error('USDZ anchoring patch failed');
  const out = { 'model.usda': strToU8(usda) };
  for (const k in files) if (k !== 'model.usda') out[k] = files[k];
  out['marker.png'] = await pngBytes(drawIosMarker(document.createElement('canvas'), meta.id));
  return new Blob([zipAligned(out)], { type: 'model/vnd.usdz+zip' });
}
