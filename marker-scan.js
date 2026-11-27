// Finds printed ArUco floor markers in the WebXR camera image (Chrome Android "camera-access")
// and turns them into a floor position + arrow direction in the AR reference space.
// Needs the classic js-aruco2 scripts (global AR / CV) loaded before this module.

const VS = `#version 300 es
in vec2 p; out vec2 uv;
void main() { uv = p * 0.5 + 0.5; gl_Position = vec4(p, 0.0, 1.0); }`;
const FS = `#version 300 es
precision mediump float;
in vec2 uv; uniform sampler2D t; out vec4 o;
void main() { o = texture(t, uv); }`;

export const MARKER_SIDE_M = 0.152;   // printed black square side (markers.html)

export class CameraMarkerScanner {
  constructor(renderer, THREE, width = 800) {
    this.r = renderer; this.T = THREE; this.gl = renderer.getContext(); this.maxW = width;
    this.det = new AR.Detector({ dictionaryName: 'ARUCO_MIP_36h12', maxHammingDistance: 2 });
    this.flip = null;   // row order of the read-back image; learned from the first marker that decodes
    this.prog = null; this.w = 0; this.h = 0;
  }

  _init() {
    const gl = this.gl;
    const sh = (type, src) => {
      const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s));
      return s;
    };
    const p = gl.createProgram();
    gl.attachShader(p, sh(gl.VERTEX_SHADER, VS)); gl.attachShader(p, sh(gl.FRAGMENT_SHADER, FS)); gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
    this.prog = p; this.uTex = gl.getUniformLocation(p, 't');
    this.vao = gl.createVertexArray(); gl.bindVertexArray(this.vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(p, 'p'); gl.enableVertexAttribArray(loc); gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
    gl.bindVertexArray(null);
    this.fbo = gl.createFramebuffer(); this.tex = gl.createTexture();
  }

  _size(w, h) {
    if (w === this.w && h === this.h) return;
    const gl = this.gl;
    gl.bindTexture(gl.TEXTURE_2D, this.tex);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.bindFramebuffer(gl.FRAMEBUFFER, this.fbo);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, this.tex, 0);
    this.w = w; this.h = h; this.px = new Uint8Array(w * h * 4); this.img = new Uint8ClampedArray(w * h * 4);
  }

  // Copies the camera image of this frame into this.px at low resolution. Restores three.js GL state.
  _read(binding, view) {
    const camTex = binding.getCameraImage(view.camera);
    if (!camTex) return false;
    const gl = this.gl;
    const prevFb = gl.getParameter(gl.FRAMEBUFFER_BINDING), prevVp = gl.getParameter(gl.VIEWPORT);
    if (!this.prog) this._init();
    const w = Math.min(this.maxW, view.camera.width), h = Math.round(view.camera.height * w / view.camera.width);
    this._size(w, h);
    gl.bindFramebuffer(gl.FRAMEBUFFER, this.fbo); gl.viewport(0, 0, w, h);
    gl.disable(gl.DEPTH_TEST); gl.disable(gl.BLEND); gl.disable(gl.CULL_FACE); gl.disable(gl.SCISSOR_TEST); gl.colorMask(true, true, true, true);
    gl.useProgram(this.prog); gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, camTex); gl.uniform1i(this.uTex, 0);
    gl.bindVertexArray(this.vao); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4); gl.bindVertexArray(null);
    gl.readPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, this.px);
    gl.bindFramebuffer(gl.FRAMEBUFFER, prevFb); gl.viewport(prevVp[0], prevVp[1], prevVp[2], prevVp[3]);
    this.r.resetState();
    this.r.setRenderTarget(this.r.getRenderTarget());   // re-bind the XR framebuffer three.js expects
    return true;
  }

  // A mirrored image never decodes, so whichever row order decodes a known marker is the upright one.
  _detect(knownIds) {
    const { w, h, px, img } = this, row = w * 4;
    for (const flip of this.flip === null ? [true, false] : [this.flip]) {
      if (flip) for (let y = 0; y < h; y++) img.set(px.subarray((h - 1 - y) * row, (h - y) * row), y * row);
      else img.set(px);
      const found = this.det.detectImage(w, h, img).filter(m => knownIds.has(m.id));
      if (found.length) { this.flip = flip; return found; }
    }
    return [];
  }

  // Ray through image pixel (top-left origin) intersected with the horizontal floor plane y = floorY.
  floorPoint(view, x, y, floorY) {
    const T = this.T;
    const inv = new T.Matrix4().fromArray(view.projectionMatrix).invert();
    const pose = new T.Matrix4().fromArray(view.transform.matrix);
    const nx = x / this.w * 2 - 1, ny = 1 - y / this.h * 2;
    const a = new T.Vector3(nx, ny, -1).applyMatrix4(inv).applyMatrix4(pose);
    const b = new T.Vector3(nx, ny, 0).applyMatrix4(inv).applyMatrix4(pose);
    const d = b.sub(a);
    if (Math.abs(d.y) < 1e-6) return null;
    const t = (floorY - a.y) / d.y;
    return t > 0 ? a.add(d.multiplyScalar(t)) : null;
  }

  // Marker corners (clockwise from the marker's top-left) on the floor -> center, arrow direction (+x of the print), side length.
  poseFromCorners(view, corners, floorY) {
    const p = corners.map(c => this.floorPoint(view, c.x, c.y, floorY));
    if (p.some(v => !v)) return null;
    const center = p[0].clone().add(p[1]).add(p[2]).add(p[3]).multiplyScalar(0.25);
    const dir = p[1].clone().sub(p[0]).add(p[2].clone().sub(p[3])); dir.y = 0; dir.normalize();
    const side = (p[0].distanceTo(p[1]) + p[1].distanceTo(p[2]) + p[2].distanceTo(p[3]) + p[3].distanceTo(p[0])) / 4;
    return { center, dir, side };
  }

  // One scan: returns [{ id, center, dir, side }] for known markers visible in this frame.
  scan(binding, view, floorY, knownIds) {
    if (!view.camera || !this._read(binding, view)) return [];
    return this._detect(knownIds)
      .map(m => ({ id: m.id, ...this.poseFromCorners(view, m.corners, floorY) }))
      .filter(m => m.center);
  }
}
