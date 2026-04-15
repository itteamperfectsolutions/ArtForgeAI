window.mockup3d = (function () {
    let scene, camera, renderer, controls;
    let currentModel = null;
    let bodyMesh = null;
    let frontFaceMesh = null;
    let backFaceMesh = null;
    let designTexture = null;
    let frontFaceTexture = null;
    let backFaceTexture = null;
    let animationId = null;
    let dotnetRef = null;

    let baseColor = '#FFFFFF';
    let mugStyle = 'straight';
    let tshirtStyle = 'crew';
    let currentModelUrl = '';
    let recordingMode = false;
    let rafScheduled = false;
    const TEX_W = 2048;
    const TEX_H = 1024;
    const designs = {
        front: { img: null, size: 0.5, v: 0.0 },
        back: { img: null, size: 0.5, v: 0.0 },
        wrap: { img: null, size: 1.0, v: 0.0 }
    };

    async function initScene(canvasId, dotnetObjRef) {
        dotnetRef = dotnetObjRef;
        const container = document.getElementById(canvasId);
        if (!container) return false;

        const testCanvas = document.createElement('canvas');
        const gl = testCanvas.getContext('webgl2') || testCanvas.getContext('webgl');
        if (!gl) return false;

        if (typeof THREE === 'undefined' || typeof GLTFLoader === 'undefined') {
            await new Promise((resolve) => {
                let tries = 0;
                const check = () => {
                    if (typeof THREE !== 'undefined' && typeof GLTFLoader !== 'undefined') return resolve();
                    if (++tries > 100) return resolve();
                    setTimeout(check, 50);
                };
                check();
            });
            if (typeof THREE === 'undefined' || typeof GLTFLoader === 'undefined') return false;
        }

        scene = new THREE.Scene();
        scene.background = new THREE.Color(0x1a1a2e);

        camera = new THREE.PerspectiveCamera(45, container.clientWidth / container.clientHeight, 0.1, 100);
        camera.position.set(0, 0.5, 2.5);

        renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true, powerPreference: 'low-power' });
        renderer.setSize(container.clientWidth, container.clientHeight);
        renderer.setPixelRatio(Math.min(window.devicePixelRatio, 1.5));
        renderer.outputColorSpace = THREE.SRGBColorSpace;
        renderer.toneMapping = THREE.ACESFilmicToneMapping;
        renderer.toneMappingExposure = 1.2;
        container.appendChild(renderer.domElement);

        controls = new OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;
        controls.dampingFactor = 0.12;
        controls.minPolarAngle = Math.PI * 0.2;
        controls.maxPolarAngle = Math.PI * 0.8;
        controls.minDistance = 1.0;
        controls.maxDistance = 5.0;
        controls.addEventListener('change', requestFrame);

        const keyLight = new THREE.DirectionalLight(0xfff5e6, 1.5);
        keyLight.position.set(3, 4, 2);
        scene.add(keyLight);

        const fillLight = new THREE.DirectionalLight(0xe6f0ff, 0.8);
        fillLight.position.set(-3, 3, 1);
        scene.add(fillLight);

        const rimLight = new THREE.DirectionalLight(0xffffff, 0.4);
        rimLight.position.set(0, 2, -3);
        scene.add(rimLight);

        const ambientLight = new THREE.AmbientLight(0xffffff, 0.3);
        scene.add(ambientLight);

        window.addEventListener('resize', onResize);
        requestFrame();
        return true;
    }

    function onResize() {
        if (!renderer) return;
        const container = renderer.domElement.parentElement;
        if (!container) return;
        const w = container.clientWidth;
        const h = container.clientHeight;
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
        renderer.setSize(w, h);
        requestFrame();
    }

    function requestFrame() {
        if (rafScheduled) return;
        rafScheduled = true;
        animationId = requestAnimationFrame(renderFrame);
    }

    function renderFrame() {
        rafScheduled = false;
        if (!renderer || !scene || !camera) return;
        let stillAnimating = false;
        if (!recordingMode && controls) {
            stillAnimating = controls.update();
        }
        renderer.render(scene, camera);
        if (recordingMode || stillAnimating) requestFrame();
    }

    function clearCurrentModel() {
        if (!currentModel) return;
        scene.remove(currentModel);
        currentModel.traverse(function (child) {
            if (child.geometry) child.geometry.dispose();
            if (child.material) {
                if (Array.isArray(child.material)) child.material.forEach(m => m.dispose());
                else child.material.dispose();
            }
        });
        if (frontFaceTexture) { frontFaceTexture.dispose(); frontFaceTexture = null; }
        if (backFaceTexture) { backFaceTexture.dispose(); backFaceTexture = null; }
        currentModel = null;
        bodyMesh = null;
        frontFaceMesh = null;
        backFaceMesh = null;
    }

    function remapExtrudeUVsToFront(geom) {
        geom.computeBoundingBox();
        const bb = geom.boundingBox;
        const sx = (bb.max.x - bb.min.x) || 1;
        const sy = (bb.max.y - bb.min.y) || 1;
        const pos = geom.attributes.position;
        const uv = geom.attributes.uv;
        for (let i = 0; i < pos.count; i++) {
            const x = pos.getX(i);
            const y = pos.getY(i);
            uv.setXY(i, (x - bb.min.x) / sx, (y - bb.min.y) / sy);
        }
        uv.needsUpdate = true;
    }

    function findBodyMesh(root) {
        let best = null;
        let bestArea = 0;
        root.traverse(function (child) {
            if (!child.isMesh) return;
            const n = (child.name || '').toLowerCase();
            if (n === 'body') { best = child; bestArea = Infinity; return; }
            if (n.includes('handle') || n.includes('rim')) return;
            const box = new THREE.Box3().setFromObject(child);
            const size = box.getSize(new THREE.Vector3());
            const area = size.x * size.y + size.y * size.z + size.x * size.z;
            if (area > bestArea) { bestArea = area; best = child; }
        });
        return best;
    }

    function loadModel(modelUrl) {
        currentModelUrl = modelUrl;
        clearCurrentModel();

        const loader = new GLTFLoader();
        loader.load(
            modelUrl,
            function (gltf) {
                currentModel = gltf.scene;
                const box = new THREE.Box3().setFromObject(currentModel);
                const center = box.getCenter(new THREE.Vector3());
                const size = box.getSize(new THREE.Vector3());
                const maxDim = Math.max(size.x, size.y, size.z);
                const scale = 1.5 / maxDim;
                currentModel.scale.setScalar(scale);
                currentModel.position.sub(center.multiplyScalar(scale));
                scene.add(currentModel);
                bodyMesh = findBodyMesh(currentModel);
                rebuildTexture();
                requestFrame();
                if (dotnetRef) dotnetRef.invokeMethodAsync('OnModelLoaded');
            },
            undefined,
            function (error) {
                console.error('Model load error:', error);
                loadFallbackModel(modelUrl);
            }
        );
    }

    function loadFallbackModel(modelUrl) {
        const isMug = modelUrl.includes('mug');

        if (isMug) {
            const group = new THREE.Group();
            const topR = 0.4;
            const botR = mugStyle === 'straight' ? 0.4 : 0.33;
            const bodyGeom = new THREE.CylinderGeometry(topR, botR, 0.95, 64, 1, true, -Math.PI / 2, Math.PI * 2);
            const bodyMat = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.35, metalness: 0.05, side: THREE.DoubleSide });
            const body = new THREE.Mesh(bodyGeom, bodyMat);
            body.name = 'body';
            group.add(body);

            const bottomGeom = new THREE.CircleGeometry(botR, 64);
            const bottom = new THREE.Mesh(bottomGeom, bodyMat.clone());
            bottom.rotation.x = Math.PI / 2;
            bottom.position.y = -0.475;
            group.add(bottom);

            const topRingGeom = new THREE.RingGeometry(topR - 0.04, topR, 64);
            const topRing = new THREE.Mesh(topRingGeom, bodyMat.clone());
            topRing.rotation.x = -Math.PI / 2;
            topRing.position.y = 0.475;
            group.add(topRing);

            const handleGeom = new THREE.TorusGeometry(0.22, 0.04, 16, 32, Math.PI);
            const handleMat = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.35, metalness: 0.05 });
            const handle = new THREE.Mesh(handleGeom, handleMat);
            handle.name = 'handle';
            handle.position.set(0.42, 0, 0);
            handle.rotation.z = -Math.PI / 2;
            group.add(handle);

            currentModel = group;
            currentModel.rotation.y = Math.PI / 2;
            scene.add(currentModel);
            bodyMesh = body;
        } else {
            const isHoodie = modelUrl.includes('hoodie');
            const group = new THREE.Group();
            const shape = new THREE.Shape();
            const hasCollar = !isHoodie && tshirtStyle === 'crew';

            if (isHoodie) {
                shape.moveTo(-0.18, 0.62);
                shape.bezierCurveTo(-0.35, 0.98, 0.35, 0.98, 0.18, 0.62);
            } else if (hasCollar) {
                shape.moveTo(-0.14, 0.62);
                shape.quadraticCurveTo(0, 0.50, 0.14, 0.62);
            } else {
                shape.moveTo(-0.28, 0.62);
                shape.lineTo(0.28, 0.62);
            }
            shape.lineTo(0.38, 0.62);
            shape.quadraticCurveTo(0.58, 0.58, 0.72, 0.42);
            shape.lineTo(0.62, 0.12);
            shape.quadraticCurveTo(0.52, 0.16, 0.42, 0.24);
            shape.lineTo(0.50, -0.64);
            shape.quadraticCurveTo(0, -0.68, -0.50, -0.64);
            shape.lineTo(-0.42, 0.24);
            shape.quadraticCurveTo(-0.52, 0.16, -0.62, 0.12);
            shape.lineTo(-0.72, 0.42);
            shape.quadraticCurveTo(-0.58, 0.58, -0.38, 0.62);
            if (isHoodie) shape.lineTo(-0.18, 0.62);
            else if (hasCollar) shape.lineTo(-0.14, 0.62);
            else shape.lineTo(-0.28, 0.62);

            const extrudeSettings = {
                depth: 0.12,
                bevelEnabled: true,
                bevelSegments: 4,
                bevelSize: 0.025,
                bevelThickness: 0.03,
                curveSegments: 18
            };
            const bodyGeom = new THREE.ExtrudeGeometry(shape, extrudeSettings);
            bodyGeom.center();

            const bodyMat = new THREE.MeshStandardMaterial({
                color: 0xffffff,
                roughness: 0.88,
                metalness: 0.0,
                side: THREE.DoubleSide
            });
            const body = new THREE.Mesh(bodyGeom, bodyMat);
            body.name = 'body';
            group.add(body);

            const faceShapeGeom = new THREE.ShapeGeometry(shape);
            faceShapeGeom.center();
            remapExtrudeUVsToFront(faceShapeGeom);

            const halfDepth = 0.075;
            const frontMat = new THREE.MeshStandardMaterial({
                color: 0xffffff,
                roughness: 0.9,
                transparent: true,
                alphaTest: 0.05,
                depthWrite: false,
                side: THREE.FrontSide
            });
            frontFaceMesh = new THREE.Mesh(faceShapeGeom, frontMat);
            frontFaceMesh.name = 'frontFace';
            frontFaceMesh.position.z = halfDepth;
            frontFaceMesh.renderOrder = 1;
            group.add(frontFaceMesh);

            const backFaceGeom = faceShapeGeom.clone();
            const backMat = new THREE.MeshStandardMaterial({
                color: 0xffffff,
                roughness: 0.9,
                transparent: true,
                alphaTest: 0.05,
                depthWrite: false,
                side: THREE.FrontSide
            });
            backFaceMesh = new THREE.Mesh(backFaceGeom, backMat);
            backFaceMesh.name = 'backFace';
            backFaceMesh.position.z = -halfDepth;
            backFaceMesh.rotation.y = Math.PI;
            backFaceMesh.renderOrder = 1;
            group.add(backFaceMesh);

            if (isHoodie) {
                const pocketShape = new THREE.Shape();
                pocketShape.moveTo(-0.30, -0.15);
                pocketShape.lineTo(0.30, -0.15);
                pocketShape.lineTo(0.26, -0.42);
                pocketShape.lineTo(-0.26, -0.42);
                pocketShape.lineTo(-0.30, -0.15);
                const pocketGeom = new THREE.ExtrudeGeometry(pocketShape, {
                    depth: 0.03, bevelEnabled: true, bevelSize: 0.01, bevelThickness: 0.01, bevelSegments: 2
                });
                const pocketMat = new THREE.MeshStandardMaterial({ color: 0xe8e8e8, roughness: 0.9 });
                const pocket = new THREE.Mesh(pocketGeom, pocketMat);
                pocket.name = 'pocket';
                pocket.position.set(0, 0, 0.09);
                group.add(pocket);

                const string1 = new THREE.Mesh(
                    new THREE.CylinderGeometry(0.008, 0.008, 0.22, 8),
                    new THREE.MeshStandardMaterial({ color: 0xeeeeee, roughness: 0.9 })
                );
                string1.position.set(-0.05, 0.48, 0.10);
                group.add(string1);
                const string2 = string1.clone();
                string2.position.set(0.05, 0.48, 0.10);
                group.add(string2);
            } else if (hasCollar) {
                const collarGeom = new THREE.TorusGeometry(0.14, 0.022, 12, 32, Math.PI);
                const collarMat = new THREE.MeshStandardMaterial({ color: 0xdddddd, roughness: 0.85 });
                const collar = new THREE.Mesh(collarGeom, collarMat);
                collar.name = 'collar';
                collar.position.set(0, 0.50, 0.07);
                collar.rotation.z = Math.PI;
                group.add(collar);
            }

            currentModel = group;
            scene.add(currentModel);
            bodyMesh = body;
        }

        rebuildTexture();
        requestFrame();
        if (dotnetRef) dotnetRef.invokeMethodAsync('OnModelLoaded');
    }

    function setColor(hexColor) {
        baseColor = hexColor;
        if (!currentModel) return;
        const isFlat = !currentModelUrl.includes('mug');
        const c = new THREE.Color(hexColor);
        if (isFlat) {
            if (bodyMesh && bodyMesh.material) {
                bodyMesh.material.color.copy(c);
                bodyMesh.material.needsUpdate = true;
            }
            requestFrame();
        } else {
            currentModel.traverse(function (child) {
                if (child.isMesh && child !== bodyMesh) {
                    if (child.material && child.material.color) child.material.color.copy(c);
                }
            });
            rebuildTexture();
        }
    }

    function rebuildTexture() {
        if (!bodyMesh) return;
        const isFlat = !currentModelUrl.includes('mug');
        if (isFlat) { rebuildFlatTextures(); return; }

        const canvas = document.createElement('canvas');
        canvas.width = TEX_W;
        canvas.height = TEX_H;
        const ctx = canvas.getContext('2d');
        ctx.fillStyle = baseColor;
        ctx.fillRect(0, 0, TEX_W, TEX_H);

        if (designs.wrap.img) {
            drawWrap(ctx, designs.wrap);
        } else {
            const handleGap = 0.14;
            const halfPrintW = TEX_W * (0.5 - handleGap / 2);
            drawDesignInRegion(ctx, designs.front, 0, halfPrintW);
            drawDesignInRegion(ctx, designs.back, TEX_W - halfPrintW, TEX_W);
        }

        if (designTexture) designTexture.dispose();
        designTexture = new THREE.CanvasTexture(canvas);
        designTexture.colorSpace = THREE.SRGBColorSpace;
        designTexture.anisotropy = 8;
        designTexture.needsUpdate = true;

        bodyMesh.material.color.set(0xffffff);
        bodyMesh.material.map = designTexture;
        bodyMesh.material.needsUpdate = true;
        requestFrame();
    }

    function rebuildFlatTextures() {
        if (!frontFaceMesh || !backFaceMesh) return;
        const isHoodie = currentModelUrl.includes('hoodie');

        const frontCanvas = document.createElement('canvas');
        frontCanvas.width = TEX_W;
        frontCanvas.height = TEX_H;
        const fctx = frontCanvas.getContext('2d');
        if (designs.wrap.img) {
            drawWrapFlat(fctx, designs.wrap);
        } else if (designs.front.img) {
            drawFrontLogo(fctx, designs.front, isHoodie);
        }
        if (frontFaceTexture) frontFaceTexture.dispose();
        frontFaceTexture = new THREE.CanvasTexture(frontCanvas);
        frontFaceTexture.colorSpace = THREE.SRGBColorSpace;
        frontFaceTexture.anisotropy = 8;
        frontFaceTexture.needsUpdate = true;
        frontFaceMesh.material.map = frontFaceTexture;
        frontFaceMesh.material.needsUpdate = true;

        const backCanvas = document.createElement('canvas');
        backCanvas.width = TEX_W;
        backCanvas.height = TEX_H;
        const bctx = backCanvas.getContext('2d');
        bctx.save();
        bctx.translate(TEX_W, 0);
        bctx.scale(-1, 1);
        if (designs.wrap.img) {
            drawWrapFlat(bctx, designs.wrap);
        } else if (designs.back.img) {
            drawBackPhoto(bctx, designs.back);
        }
        bctx.restore();
        if (backFaceTexture) backFaceTexture.dispose();
        backFaceTexture = new THREE.CanvasTexture(backCanvas);
        backFaceTexture.colorSpace = THREE.SRGBColorSpace;
        backFaceTexture.anisotropy = 8;
        backFaceTexture.needsUpdate = true;
        backFaceMesh.material.map = backFaceTexture;
        backFaceMesh.material.needsUpdate = true;

        if (bodyMesh && bodyMesh.material && bodyMesh.material.color) {
            bodyMesh.material.color.set(baseColor);
            bodyMesh.material.map = null;
            bodyMesh.material.needsUpdate = true;
        }
        requestFrame();
    }

    function drawFrontLogo(ctx, slot, isHoodie) {
        const img = slot.img;
        const aspect = img.width / img.height;
        const base = TEX_W * 0.18;
        let w = base * slot.size;
        let h = w / aspect;
        const maxH = TEX_H * 0.35;
        if (h > maxH) { h = maxH; w = h * aspect; }
        const cx = TEX_W * 0.50 + TEX_W * 0.14;
        const cy = (isHoodie ? TEX_H * 0.56 : TEX_H * 0.30) - (slot.v * TEX_H * 0.15);
        ctx.drawImage(img, cx - w / 2, cy - h / 2, w, h);
    }

    function drawBackPhoto(ctx, slot) {
        const img = slot.img;
        const aspect = img.width / img.height;
        const maxW = TEX_W * 0.65 * slot.size;
        const maxH = TEX_H * 0.70 * slot.size;
        let w = maxW;
        let h = w / aspect;
        if (h > maxH) { h = maxH; w = h * aspect; }
        const cx = TEX_W * 0.5;
        const cy = TEX_H * 0.50 - (slot.v * TEX_H * 0.20);
        ctx.drawImage(img, cx - w / 2, cy - h / 2, w, h);
    }

    function drawDesignFlatCenter(ctx, slot) {
        if (!slot.img) return;
        const img = slot.img;
        const aspect = img.width / img.height;
        const chestW = TEX_W * 0.40;
        const chestH = TEX_H * 0.42;
        let w = chestW;
        let h = w / aspect;
        if (h > chestH) { h = chestH; w = h * aspect; }
        const cx = TEX_W * 0.5;
        const cy = TEX_H * 0.45;
        ctx.drawImage(img, cx - w / 2, cy - h / 2, w, h);
    }

    function drawWrapFlat(ctx, slot) {
        if (!slot.img) return;
        const img = slot.img;
        ctx.drawImage(img, 0, 0, TEX_W, TEX_H);
    }

    function drawDesignInRegion(ctx, slot, xMin, xMax) {
        if (!slot.img) return;
        const img = slot.img;
        const regionW = xMax - xMin;
        const scale = TEX_H / img.height;
        const drawW = img.width * scale;
        ctx.save();
        ctx.beginPath();
        ctx.rect(xMin, 0, regionW, TEX_H);
        ctx.clip();
        ctx.drawImage(img, xMin, 0, drawW, TEX_H);
        ctx.restore();
    }

    function drawDesignAtSeam(ctx, slot) {
        if (!slot.img) return;
        const img = slot.img;
        const aspect = img.width / img.height;
        const maxHalfW = TEX_W * 0.18;
        const maxH = TEX_H * 0.70;
        let w = (TEX_W * 0.22) * slot.size;
        let h = w / aspect;
        if (h > maxH) { h = maxH; w = h * aspect; }
        if (w / 2 > maxHalfW) { w = maxHalfW * 2; h = w / aspect; }
        const centerY = TEX_H * 0.5 - (slot.v * TEX_H * 0.30);
        const halfW = w / 2;
        const imgHalfW = img.width / 2;
        ctx.drawImage(img, imgHalfW, 0, imgHalfW, img.height,
            0, centerY - h / 2, halfW, h);
        ctx.drawImage(img, 0, 0, imgHalfW, img.height,
            TEX_W - halfW, centerY - h / 2, halfW, h);
    }

    function drawWrap(ctx, slot) {
        const img = slot.img;
        const vShift = slot.v * TEX_H * 0.15;
        const handleGap = 0.14;
        const halfPrintW = TEX_W * (0.5 - handleGap / 2);
        const imgHalfW = img.width / 2;
        ctx.drawImage(img,
            imgHalfW, 0, imgHalfW, img.height,
            0, vShift, halfPrintW, TEX_H);
        ctx.drawImage(img,
            0, 0, imgHalfW, img.height,
            TEX_W - halfPrintW, vShift, halfPrintW, TEX_H);
    }

    function drawDesign(ctx, slot, centerX) {
        if (!slot.img) return;
        const img = slot.img;
        const aspect = img.width / img.height;
        const maxHalfW = TEX_W * 0.20;
        const maxH = TEX_H * 0.70;
        let w = (TEX_W * 0.22) * slot.size;
        let h = w / aspect;
        if (h > maxH) {
            h = maxH;
            w = h * aspect;
        }
        if (w / 2 > maxHalfW) {
            w = maxHalfW * 2;
            h = w / aspect;
        }
        const centerY = TEX_H * 0.5 - (slot.v * TEX_H * 0.30);
        ctx.drawImage(img, centerX - w / 2, centerY - h / 2, w, h);
    }

    function setDesign(slot, dataUrl) {
        if (!designs[slot]) return;
        if (!dataUrl) { designs[slot].img = null; rebuildTexture(); return; }
        const img = new Image();
        img.crossOrigin = 'anonymous';
        img.onload = function () {
            designs[slot].img = img;
            rebuildTexture();
        };
        img.src = dataUrl;
    }

    function clearDesign(slot) {
        if (!designs[slot]) return;
        designs[slot].img = null;
        rebuildTexture();
    }

    function updateDesignTransform(slot, size, v) {
        if (!designs[slot]) return;
        designs[slot].size = size;
        designs[slot].v = v;
        rebuildTexture();
    }

    async function captureRotationVideo(durationSec, fps) {
        if (!renderer || !scene || !camera || !controls) return null;
        if (typeof MediaRecorder === 'undefined') return null;

        const recW = 720;
        const recH = 1280;
        const origW = renderer.domElement.width;
        const origH = renderer.domElement.height;
        const origAspect = camera.aspect;
        renderer.setSize(recW, recH, false);
        camera.aspect = recW / recH;
        camera.updateProjectionMatrix();

        const stream = renderer.domElement.captureStream(fps);
        const mp4Candidates = [
            'video/mp4;codecs=avc1.42E01E',
            'video/mp4;codecs=avc1',
            'video/mp4'
        ];
        const webmCandidates = [
            'video/webm;codecs=vp9',
            'video/webm;codecs=vp8',
            'video/webm'
        ];
        let mimeType = null;
        let ext = 'mp4';
        for (const m of mp4Candidates) {
            if (MediaRecorder.isTypeSupported(m)) { mimeType = m; ext = 'mp4'; break; }
        }
        if (!mimeType) {
            for (const m of webmCandidates) {
                if (MediaRecorder.isTypeSupported(m)) { mimeType = m; ext = 'webm'; break; }
            }
        }
        if (!mimeType) return null;

        const recorder = new MediaRecorder(stream, { mimeType, videoBitsPerSecond: 3_500_000 });
        const chunks = [];
        recorder.ondataavailable = e => { if (e.data && e.data.size > 0) chunks.push(e.data); };

        const radius = Math.sqrt(camera.position.x * camera.position.x + camera.position.z * camera.position.z) || 2.5;
        const camY = camera.position.y;
        const handleGap = 0.14;
        const maxAngle = Math.PI * (1 - handleGap);

        const prevEnabled = controls.enabled;
        recordingMode = true;
        controls.enabled = false;

        let rafId = 0;
        const startTime = performance.now();
        const totalMs = Math.round(durationSec * 1000);

        function setCameraAngle(angle) {
            camera.position.set(Math.sin(angle) * radius, camY, Math.cos(angle) * radius);
            camera.lookAt(0, 0, 0);
        }

        function tickCamera() {
            const elapsed = performance.now() - startTime;
            const t = Math.min(elapsed / totalMs, 1);
            const angle = -maxAngle * Math.cos(t * Math.PI);
            setCameraAngle(angle);
            requestFrame();
            if (t < 1) rafId = requestAnimationFrame(tickCamera);
        }

        return new Promise((resolve) => {
            recorder.onstop = () => {
                if (rafId) cancelAnimationFrame(rafId);
                recordingMode = false;
                controls.enabled = prevEnabled;
                renderer.setSize(origW, origH, false);
                camera.aspect = origAspect;
                camera.updateProjectionMatrix();
                onResize();
                setCameraAngle(0);
                const blob = new Blob(chunks, { type: mimeType });
                const reader = new FileReader();
                reader.onloadend = () => {
                    const dataUrl = reader.result;
                    const b64 = dataUrl.substring(dataUrl.indexOf(',') + 1);
                    resolve({ data: b64, ext: ext, mime: mimeType.split(';')[0] });
                };
                reader.readAsDataURL(blob);
            };
            setCameraAngle(-maxAngle);
            recorder.start();
            tickCamera();
            setTimeout(() => {
                try { recorder.stop(); } catch (_) { }
            }, totalMs);
        });
    }

    let _pendingShare = null;

    function pickAndCompressImage(slot, dotnetRef, maxBytes) {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = '.jpg,.jpeg,.png,.webp';
        input.style.display = 'none';
        document.body.appendChild(input);
        input.addEventListener('change', async () => {
            const file = input.files && input.files[0];
            document.body.removeChild(input);
            if (!file) return;
            try {
                const result = await readAndCompressFile(file, maxBytes);
                if (dotnetRef) {
                    await dotnetRef.invokeMethodAsync('OnClientImagePicked', slot, result.dataUrl, result.compressed, file.size, result.outSize);
                }
            } catch (err) {
                console.error('pickAndCompressImage failed', err);
                if (dotnetRef) {
                    try { await dotnetRef.invokeMethodAsync('OnClientImageError', 'Failed to read file: ' + (err && err.message ? err.message : err)); } catch (_) { }
                }
            }
        }, { once: true });
        input.click();
    }

    async function readAndCompressFile(file, maxBytes) {
        const origDataUrl = await new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onload = () => resolve(reader.result);
            reader.onerror = () => reject(reader.error || new Error('read failed'));
            reader.readAsDataURL(file);
        });
        if (file.size <= maxBytes) {
            return { dataUrl: origDataUrl, compressed: false, outSize: file.size };
        }
        const img = await new Promise((resolve, reject) => {
            const image = new Image();
            image.onload = () => resolve(image);
            image.onerror = () => reject(new Error('image decode failed'));
            image.src = origDataUrl;
        });
        const hasAlpha = origDataUrl.startsWith('data:image/png') || origDataUrl.startsWith('data:image/webp');
        const outMime = hasAlpha ? 'image/webp' : 'image/jpeg';
        const maxDim = 2400;
        let w = img.width, h = img.height;
        if (Math.max(w, h) > maxDim) {
            const s = maxDim / Math.max(w, h);
            w = Math.round(w * s);
            h = Math.round(h * s);
        }
        const canvas = document.createElement('canvas');
        canvas.width = w; canvas.height = h;
        const ctx = canvas.getContext('2d');
        ctx.drawImage(img, 0, 0, w, h);
        let quality = 0.90;
        let dataUrl = canvas.toDataURL(outMime, quality);
        const estBytes = (u) => Math.floor(u.length * 0.75);
        while (estBytes(dataUrl) > maxBytes && quality > 0.35) {
            quality -= 0.1;
            dataUrl = canvas.toDataURL(outMime, quality);
        }
        let scale = 1.0;
        while (estBytes(dataUrl) > maxBytes && scale > 0.3) {
            scale *= 0.8;
            canvas.width = Math.max(1, Math.round(w * scale));
            canvas.height = Math.max(1, Math.round(h * scale));
            ctx.drawImage(img, 0, 0, canvas.width, canvas.height);
            dataUrl = canvas.toDataURL(outMime, 0.85);
        }
        return { dataUrl: dataUrl, compressed: true, outSize: estBytes(dataUrl) };
    }

    function openWhatsAppWeb() {
        try { window.open('https://web.whatsapp.com/', '_blank'); } catch (_) { }
    }

    function cacheShareFile(base64, mime, filename, title) {
        try {
            const bin = atob(base64);
            const arr = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
            _pendingShare = {
                file: new File([arr], filename, { type: mime }),
                title: title,
                filename: filename,
                mime: mime,
                base64: base64
            };
            return true;
        } catch (e) {
            console.error('cacheShareFile failed', e);
            return false;
        }
    }

    async function shareCachedFile() {
        if (!_pendingShare) return 'no-cache';
        try {
            if (navigator.canShare && navigator.canShare({ files: [_pendingShare.file] })) {
                await navigator.share({ files: [_pendingShare.file], title: _pendingShare.title, text: _pendingShare.title });
                return 'shared';
            }
            return 'unsupported';
        } catch (e) {
            if (e && e.name === 'AbortError') return 'cancelled';
            console.error('share failed', e);
            return 'error';
        }
    }

    function downloadCachedFile() {
        if (!_pendingShare) return false;
        const p = _pendingShare;
        if (typeof window.downloadFileFromBytes === 'function') {
            window.downloadFileFromBytes(p.filename, p.mime, p.base64);
            return true;
        }
        return false;
    }

    function clearCachedShare() { _pendingShare = null; }

    async function shareVideoFile(base64, mime, filename, title) {
        try {
            const bin = atob(base64);
            const arr = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
            const file = new File([arr], filename, { type: mime });
            if (navigator.canShare && navigator.canShare({ files: [file] })) {
                await navigator.share({ files: [file], title: title, text: title });
                return 'shared';
            }
            return 'unsupported';
        } catch (e) {
            if (e && e.name === 'AbortError') return 'cancelled';
            console.error('share failed', e);
            return 'error';
        }
    }

    async function transcodeToMp4(base64Webm) {
        try {
            const bin = atob(base64Webm);
            const arr = new Uint8Array(bin.length);
            for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
            const resp = await fetch('/api/mockup3d/transcode', {
                method: 'POST',
                headers: { 'Content-Type': 'application/octet-stream' },
                body: arr
            });
            if (!resp.ok) return null;
            const buf = await resp.arrayBuffer();
            const bytes = new Uint8Array(buf);
            let binary = '';
            const chunkSize = 0x8000;
            for (let i = 0; i < bytes.length; i += chunkSize) {
                binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
            }
            return btoa(binary);
        } catch (e) {
            console.error('transcodeToMp4 failed', e);
            return null;
        }
    }

    function captureSnapshot(width, height) {
        if (!renderer || !scene || !camera) return null;
        const origW = renderer.domElement.width;
        const origH = renderer.domElement.height;
        renderer.setSize(width, height);
        camera.aspect = width / height;
        camera.updateProjectionMatrix();
        renderer.render(scene, camera);
        const dataUrl = renderer.domElement.toDataURL('image/png');
        renderer.setSize(origW, origH);
        camera.aspect = origW / origH;
        camera.updateProjectionMatrix();
        return dataUrl.split(',')[1];
    }

    function dispose() {
        if (animationId) cancelAnimationFrame(animationId);
        window.removeEventListener('resize', onResize);
        if (designTexture) { designTexture.dispose(); designTexture = null; }
        designs.front.img = null;
        designs.back.img = null;
        clearCurrentModel();
        if (renderer) { renderer.dispose(); renderer.domElement.remove(); renderer = null; }
        if (controls) { controls.dispose(); controls = null; }
        scene = null;
        camera = null;
        dotnetRef = null;
    }

    function setMugStyle(style) {
        mugStyle = style === 'straight' ? 'straight' : 'tapered';
        if (currentModelUrl && currentModelUrl.includes('mug')) {
            loadModel(currentModelUrl);
        }
    }

    function setTshirtStyle(style) {
        tshirtStyle = style === 'scoop' ? 'scoop' : 'crew';
        if (currentModelUrl && (currentModelUrl.includes('tshirt') || currentModelUrl.includes('hoodie'))) {
            loadModel(currentModelUrl);
        }
    }

    return {
        initScene,
        loadModel,
        setColor,
        setDesign,
        clearDesign,
        updateDesignTransform,
        setMugStyle,
        setTshirtStyle,
        pickAndCompressImage,
        captureSnapshot,
        captureRotationVideo,
        transcodeToMp4,
        shareVideoFile,
        openWhatsAppWeb,
        cacheShareFile,
        shareCachedFile,
        downloadCachedFile,
        clearCachedShare,
        dispose
    };
})();
