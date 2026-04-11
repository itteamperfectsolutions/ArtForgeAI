window.mockup3d = (function () {
    let scene, camera, renderer, controls;
    let currentModel = null;
    let logoMesh = null;
    let logoTexture = null;
    let animationId = null;
    let dotnetRef = null;

    let logoSize = 0.5;
    let logoVPos = 0.0;
    let logoHPos = 0.0;

    function initScene(canvasId, dotnetObjRef) {
        dotnetRef = dotnetObjRef;
        const container = document.getElementById(canvasId);
        if (!container) return false;

        const testCanvas = document.createElement('canvas');
        const gl = testCanvas.getContext('webgl2') || testCanvas.getContext('webgl');
        if (!gl) return false;

        scene = new THREE.Scene();
        scene.background = new THREE.Color(0x1a1a2e);

        camera = new THREE.PerspectiveCamera(45, container.clientWidth / container.clientHeight, 0.1, 100);
        camera.position.set(0, 0.5, 2.5);

        renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
        renderer.setSize(container.clientWidth, container.clientHeight);
        renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
        renderer.outputColorSpace = THREE.SRGBColorSpace;
        renderer.toneMapping = THREE.ACESFilmicToneMapping;
        renderer.toneMappingExposure = 1.2;
        container.appendChild(renderer.domElement);

        controls = new THREE.OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;
        controls.dampingFactor = 0.08;
        controls.minPolarAngle = Math.PI * 0.2;
        controls.maxPolarAngle = Math.PI * 0.8;
        controls.minDistance = 1.0;
        controls.maxDistance = 5.0;

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
        animate();
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
    }

    function animate() {
        animationId = requestAnimationFrame(animate);
        if (controls) controls.update();
        if (renderer && scene && camera) renderer.render(scene, camera);
    }

    function loadModel(modelUrl) {
        if (currentModel) {
            scene.remove(currentModel);
            currentModel.traverse(function (child) {
                if (child.geometry) child.geometry.dispose();
                if (child.material) {
                    if (Array.isArray(child.material)) child.material.forEach(m => m.dispose());
                    else child.material.dispose();
                }
            });
            currentModel = null;
        }
        removeLogo();

        const loader = new THREE.GLTFLoader();
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
                if (logoTexture) applyLogoToModel();
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
            const bodyGeom = new THREE.CylinderGeometry(0.4, 0.35, 0.7, 32);
            const bodyMat = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.3, metalness: 0.05 });
            const body = new THREE.Mesh(bodyGeom, bodyMat);
            body.name = 'body';
            group.add(body);

            const handleGeom = new THREE.TorusGeometry(0.2, 0.04, 12, 24, Math.PI);
            const handleMat = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.3, metalness: 0.05 });
            const handle = new THREE.Mesh(handleGeom, handleMat);
            handle.position.set(0.45, 0, 0);
            handle.rotation.z = Math.PI / 2;
            group.add(handle);

            const rimGeom = new THREE.TorusGeometry(0.38, 0.02, 8, 32);
            const rimMat = new THREE.MeshStandardMaterial({ color: 0xdddddd, roughness: 0.2 });
            const rim = new THREE.Mesh(rimGeom, rimMat);
            rim.position.y = 0.35;
            rim.rotation.x = Math.PI / 2;
            group.add(rim);

            currentModel = group;
            scene.add(currentModel);
        } else {
            const group = new THREE.Group();
            const bodyGeom = new THREE.PlaneGeometry(1.2, 1.4, 1, 1);
            const bodyMat = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.8, metalness: 0.0, side: THREE.DoubleSide });
            const body = new THREE.Mesh(bodyGeom, bodyMat);
            body.name = 'body';
            group.add(body);

            const sleeveGeom = new THREE.PlaneGeometry(0.5, 0.4);
            const sleeveMat = new THREE.MeshStandardMaterial({ color: 0xffffff, roughness: 0.8, side: THREE.DoubleSide });
            const leftSleeve = new THREE.Mesh(sleeveGeom, sleeveMat);
            leftSleeve.position.set(-0.8, 0.35, 0);
            leftSleeve.rotation.z = -0.4;
            group.add(leftSleeve);

            const rightSleeve = new THREE.Mesh(sleeveGeom.clone(), sleeveMat.clone());
            rightSleeve.position.set(0.8, 0.35, 0);
            rightSleeve.rotation.z = 0.4;
            group.add(rightSleeve);

            currentModel = group;
            scene.add(currentModel);
        }

        if (logoTexture) applyLogoToModel();
        if (dotnetRef) dotnetRef.invokeMethodAsync('OnModelLoaded');
    }

    function setColor(hexColor) {
        if (!currentModel) return;
        const color = new THREE.Color(hexColor);
        currentModel.traverse(function (child) {
            if (child.isMesh && child.name !== 'logo') {
                child.material.color.copy(color);
            }
        });
    }

    function setLogo(dataUrl) {
        if (!dataUrl) { removeLogo(); return; }
        const img = new Image();
        img.crossOrigin = 'anonymous';
        img.onload = function () {
            if (logoTexture) logoTexture.dispose();
            const canvas = document.createElement('canvas');
            canvas.width = img.width;
            canvas.height = img.height;
            const ctx = canvas.getContext('2d');
            ctx.drawImage(img, 0, 0);
            logoTexture = new THREE.CanvasTexture(canvas);
            logoTexture.colorSpace = THREE.SRGBColorSpace;
            applyLogoToModel();
        };
        img.src = dataUrl;
    }

    function applyLogoToModel() {
        if (!currentModel || !logoTexture) return;
        removeLogo();
        const aspect = logoTexture.image.width / logoTexture.image.height;
        const planeW = logoSize * aspect;
        const planeH = logoSize;
        const geometry = new THREE.PlaneGeometry(planeW, planeH);
        const material = new THREE.MeshBasicMaterial({
            map: logoTexture, transparent: true, depthWrite: false,
            polygonOffset: true, polygonOffsetFactor: -1
        });
        logoMesh = new THREE.Mesh(geometry, material);
        logoMesh.name = 'logo';
        positionLogo();
        scene.add(logoMesh);
    }

    function positionLogo() {
        if (!logoMesh || !currentModel) return;
        const box = new THREE.Box3().setFromObject(currentModel);
        const center = box.getCenter(new THREE.Vector3());
        const size = box.getSize(new THREE.Vector3());
        logoMesh.position.set(
            center.x + (logoHPos * size.x * 0.4),
            center.y + (logoVPos * size.y * 0.4),
            box.max.z + 0.01
        );
        const aspect = logoTexture.image.width / logoTexture.image.height;
        logoMesh.geometry.dispose();
        logoMesh.geometry = new THREE.PlaneGeometry(logoSize * aspect, logoSize);
    }

    function updateLogoTransform(size, vPos, hPos) {
        logoSize = size;
        logoVPos = vPos;
        logoHPos = hPos;
        if (logoMesh) positionLogo();
    }

    function removeLogo() {
        if (logoMesh) {
            scene.remove(logoMesh);
            if (logoMesh.geometry) logoMesh.geometry.dispose();
            if (logoMesh.material) logoMesh.material.dispose();
            logoMesh = null;
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
        if (logoMesh) removeLogo();
        if (logoTexture) { logoTexture.dispose(); logoTexture = null; }
        if (currentModel) {
            scene.remove(currentModel);
            currentModel.traverse(function (child) {
                if (child.geometry) child.geometry.dispose();
                if (child.material) {
                    if (Array.isArray(child.material)) child.material.forEach(m => m.dispose());
                    else child.material.dispose();
                }
            });
            currentModel = null;
        }
        if (renderer) { renderer.dispose(); renderer.domElement.remove(); renderer = null; }
        if (controls) { controls.dispose(); controls = null; }
        scene = null;
        camera = null;
        dotnetRef = null;
    }

    return { initScene, loadModel, setColor, setLogo, updateLogoTransform, captureSnapshot, dispose };
})();
