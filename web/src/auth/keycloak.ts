import Keycloak from 'keycloak-js'

const keycloak = new Keycloak({
  url: import.meta.env.VITE_KEYCLOAK_URL || 'http://localhost:8080',
  realm: import.meta.env.VITE_KEYCLOAK_REALM || 'ebank',
  clientId: import.meta.env.VITE_KEYCLOAK_CLIENT_ID || 'ebank-web',
})

let initialization: Promise<boolean> | undefined

export function initializeKeycloak() {
  initialization ??= keycloak.init({
    onLoad: 'check-sso',
    pkceMethod: 'S256',
    checkLoginIframe: false,
  })
  return initialization
}

export default keycloak
