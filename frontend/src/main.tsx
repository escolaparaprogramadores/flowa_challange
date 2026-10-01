import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource/sora/latin-600.css';
import '@fontsource/sora/latin-700.css';
import '@fontsource/manrope/latin-400.css';
import '@fontsource/manrope/latin-500.css';
import '@fontsource/manrope/latin-600.css';
import '@fontsource/manrope/latin-700.css';
import '@fontsource/manrope/latin-800.css';
import './tema-base.css';
import { PaginaDaBoletaEExposicao } from './App';

createRoot(document.getElementById('raiz')!).render(
  <StrictMode>
    <PaginaDaBoletaEExposicao />
  </StrictMode>,
);
