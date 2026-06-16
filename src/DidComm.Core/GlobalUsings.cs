global using System.Buffers.Binary;
global using System.Security.Cryptography;
global using System.Text;

// The DIDComm envelope layer delegates all JOSE/crypto to DataProofsDotnet.Jose (on NetCrypto).
// Its Jwk model is a superset of didcomm's old one (adds the symmetric `k` member) with identical
// JSON members + lossless extension-data round-trip (FR-MSG-15), so it is the one JWK type the
// facade, secrets, resolver, and envelope surfaces all speak — no conversion at the boundary.
global using Jwk = DataProofsDotnet.Jose.Jwk;
