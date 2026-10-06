import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@fontsource/sora/latin-600.css';
import '@fontsource/sora/latin-700.css';
import '@fontsource/manrope/latin-400.css';
import '@fontsource/manrope/latin-500.css';
import '@fontsource/manrope/latin-600.css';
import '@fontsource/manrope/latin-700.css';
import '@fontsource/manrope/latin-800.css';
import './styles/base-theme.css';
import { OrderTicketPage } from './pages/OrderTicketPage';

// The "raiz" id is read by tests/IntegrationTests (ComposeTests), outside the screen code.
createRoot(document.getElementById('raiz')!).render(
  <StrictMode>
    <OrderTicketPage />
  </StrictMode>,
);
