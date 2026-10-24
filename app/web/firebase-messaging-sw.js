// Recebe os avisos do Kubico (Firebase Cloud Messaging) com o separador da app fechado.
// A versão tem de acompanhar a do Firebase JS SDK usada pelo firebase_core_web (13.0.0).
importScripts('https://www.gstatic.com/firebasejs/13.0.0/firebase-app-compat.js');
importScripts('https://www.gstatic.com/firebasejs/13.0.0/firebase-messaging-compat.js');

// Configuração pública da app web (a mesma de lib/firebase_options.dart; não é secreta).
firebase.initializeApp({
  apiKey: 'AIzaSyDL9eUasObNaXAksHrIDEuVvaApuqfdXhI',
  appId: '1:451544355263:web:da4ad6a4104a76c6a8d10a',
  messagingSenderId: '451544355263',
  projectId: 'kubico-casa',
  authDomain: 'kubico-casa.firebaseapp.com',
  storageBucket: 'kubico-casa.firebasestorage.app',
});

// As mensagens trazem título e texto ("notification"): o browser mostra-as sem mais código.
firebase.messaging();
