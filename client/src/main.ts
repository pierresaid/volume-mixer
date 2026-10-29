import './lib/handoff' // first: settles the token and any handed-over layout before other modules read storage
import { createApp } from 'vue'
import App from './App.vue'
import './style.css'

createApp(App).mount('#app')
