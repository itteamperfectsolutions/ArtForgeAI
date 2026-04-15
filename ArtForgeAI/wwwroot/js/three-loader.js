import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

window.THREE = THREE;
window.OrbitControls = OrbitControls;
window.GLTFLoader = GLTFLoader;
window.dispatchEvent(new Event('three-ready'));
