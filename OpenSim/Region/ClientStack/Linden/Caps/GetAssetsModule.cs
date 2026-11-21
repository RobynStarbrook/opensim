/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using Mono.Addins;
using OpenSim.Framework.Monitoring;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Capabilities.Handlers;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "GetAssetsModule")]
    public class GetAssetsModule : INonSharedRegionModule
    {
//        private static readonly ILog m_log =
//            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene m_scene;
        private bool m_Enabled;

        private string m_GetTextureURL;
        private string m_GetMeshURL;
        private string m_GetMesh2URL;
        private string m_GetAssetURL;

        class APollRequest
        {
            public PollServiceAssetEventArgs thepoll;
            public UUID reqID;
            public OSHttpRequest request;
        }

        public class APollResponse
        {
            public OSHttpResponse osresponse;
        }

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static IAssetService m_assetService = null;
        private static GetAssetsHandler m_getAssetHandler;
        private static ObjectJobEngine m_workerpool = null;
        private static int m_NumberScenes = 0;
        private static object m_loadLock = new object();
        protected IUserManagement m_UserManagement = null;

        /// <summary>
        /// Per agent byte rate limit for asset caps (GetTexture, GetMesh, GetMesh2, ViewerAsset).
        /// 0 disables. Viewers that open many parallel connections can otherwise saturate the
        /// server uplink, since these HTTP responses are not covered by the UDP throttles.
        /// </summary>
        private static int m_maxBytesPerSecPerAgent = 0;
        private static readonly Dictionary<UUID, AgentAssetBudget> m_agentBudgets = new();

        /// <summary>
        /// Token bucket shared by all asset caps of one agent, across all regions of this instance.
        /// Tokens may go negative so assets larger than the bucket still get sent; the debt
        /// then delays the agent's following responses.
        /// </summary>
        private sealed class AgentAssetBudget
        {
            public int RefCount;
            private readonly object m_lock = new();
            private readonly double m_ratePerMS;
            private readonly double m_burst;
            private double m_tokens;
            private double m_lastMS;

            public AgentAssetBudget(int bytesPerSec)
            {
                m_ratePerMS = bytesPerSec * 1e-3;
                m_burst = bytesPerSec; // one second worth
                m_tokens = m_burst;
                m_lastMS = Util.GetTimeStampMS();
            }

            public bool TryConsume(int bytes)
            {
                lock (m_lock)
                {
                    double now = Util.GetTimeStampMS();
                    m_tokens = Math.Min(m_burst, m_tokens + (now - m_lastMS) * m_ratePerMS);
                    m_lastMS = now;
                    if (m_tokens <= 0)
                        return false;
                    m_tokens -= bytes;
                    return true;
                }
            }
        }

        private static AgentAssetBudget AcquireBudget(UUID agentID)
        {
            if (m_maxBytesPerSecPerAgent <= 0)
                return null;
            lock (m_agentBudgets)
            {
                if (!m_agentBudgets.TryGetValue(agentID, out AgentAssetBudget budget))
                {
                    budget = new AgentAssetBudget(m_maxBytesPerSecPerAgent);
                    m_agentBudgets[agentID] = budget;
                }
                budget.RefCount++;
                return budget;
            }
        }

        private static void ReleaseBudget(UUID agentID, Caps _)
        {
            lock (m_agentBudgets)
            {
                if (m_agentBudgets.TryGetValue(agentID, out AgentAssetBudget budget) && --budget.RefCount <= 0)
                    m_agentBudgets.Remove(agentID);
            }
        }

        #region Region Module interfaceBase Members

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ClientStack.LindenCaps"];
            if (config == null)
                return;

            m_GetTextureURL = config.GetString("Cap_GetTexture", string.Empty);
            if (m_GetTextureURL != string.Empty)
                m_Enabled = true;

            m_GetMeshURL = config.GetString("Cap_GetMesh", string.Empty);
            if (m_GetMeshURL != string.Empty)
                m_Enabled = true;

            m_GetMesh2URL = config.GetString("Cap_GetMesh2", string.Empty);
            if (m_GetMesh2URL != string.Empty)
                m_Enabled = true;

            m_GetAssetURL = config.GetString("Cap_GetAsset", string.Empty);
            if (m_GetAssetURL != string.Empty)
                m_Enabled = true;

            int maxbps = config.GetInt("AssetsMaxBytesPerSecPerAgent", 0);
            if (maxbps > 0 && maxbps < 32000)
                maxbps = 32000; // below ~256kbps viewers start timing out fetches
            m_maxBytesPerSecPerAgent = maxbps > 0 ? maxbps : 0;
        }

        public void AddRegion(Scene pScene)
        {
            if (!m_Enabled)
                return;

            m_scene = pScene;
        }

        public void RemoveRegion(Scene s)
        {
            if (!m_Enabled)
                return;

            s.EventManager.OnRegisterCaps -= RegisterCaps;
            s.EventManager.OnDeregisterCaps -= ReleaseBudget;
            m_NumberScenes--;
            m_scene = null;
        }

        public void RegionLoaded(Scene s)
        {
            if (!m_Enabled)
                return;

            lock(m_loadLock)
            {
                if (m_assetService == null)
                {
                    m_assetService = s.RequestModuleInterface<IAssetService>();
                    // We'll reuse the same handler for all requests.
                    m_getAssetHandler = new GetAssetsHandler(m_assetService);
                }

                if (m_assetService == null)
                {
                    m_Enabled = false;
                    return;
                }

                if(m_UserManagement == null)
                    m_UserManagement = s.RequestModuleInterface<IUserManagement>();

                s.EventManager.OnRegisterCaps += RegisterCaps;
                if (m_maxBytesPerSecPerAgent > 0)
                {
                    s.EventManager.OnDeregisterCaps += ReleaseBudget;
                    m_log.Info($"[GETASSETS]: asset caps limited to {m_maxBytesPerSecPerAgent} bytes/s per agent in {s.Name}");
                }

                m_NumberScenes++;

                if (m_workerpool == null)
                    m_workerpool = new ObjectJobEngine(DoAssetRequests, "GetCapsAssetWorker", 1000, 3);
            }
        }

        public void Close()
        {
            if(m_NumberScenes <= 0 && m_workerpool != null)
            {
                m_workerpool.Dispose();
                m_workerpool = null;
            }
        }

        public string Name { get { return "GetAssetsModule"; } }

        #endregion

        private static void DoAssetRequests(object o)
        {
            if (m_NumberScenes <= 0)
                return;
            APollRequest poolreq = o as APollRequest;
            if (poolreq != null && !poolreq.reqID.IsZero())
                poolreq.thepoll.Process(poolreq);
        }

        private class PollServiceAssetEventArgs : PollServiceEventArgs
        {
            //private List<Hashtable> requests = new List<Hashtable>();
            private List<OSHttpRequest> requests = new List<OSHttpRequest>();
            private Dictionary<UUID, APollResponse> responses =new Dictionary<UUID, APollResponse>();
            private HashSet<UUID> dropedResponses = new HashSet<UUID>();

            private Scene m_scene;
            private string m_hgassets = null;
            private readonly AgentAssetBudget m_budget;
            public PollServiceAssetEventArgs(string uri, UUID pId, Scene scene, string HGAssetSVC, AgentAssetBudget budget) :
                base(null, uri, null, null, null, null, pId, int.MaxValue)
            {
                m_scene = scene;
                m_hgassets = HGAssetSVC;
                m_budget = budget;

                // GetEvents is called right after this returns true, so charging the budget here is safe.
                // If over budget the poll manager requeues the request and asks again in ~100ms.
                HasEvents = delegate(UUID requestID, UUID _)
                {
                    lock (responses)
                    {
                        if (!responses.TryGetValue(requestID, out APollResponse apr))
                            return false;
                        if (m_budget is null)
                            return true;
                        return m_budget.TryConsume(apr.osresponse.RawBufferLen);
                    }
                };

                Drop = delegate(UUID requestID, UUID _)
                {
                    lock (responses)
                    {
                        responses.Remove(requestID);
                        lock(dropedResponses)
                            dropedResponses.Add(requestID);
                    }
                };

                GetEvents = delegate(UUID requestID, UUID _)
                {
                    lock (responses)
                    {
                        if(responses.Remove(requestID, out APollResponse apr))
                        {
                            OSHttpResponse response = apr.osresponse;
                            if (response.Priority < 0)
                                response.Priority = 0;

                            Hashtable lixo = new()
                            {
                                ["h"] = response
                            };
                            return lixo;
                        }
                    }
                    return new Hashtable();
                };
                
                Request = delegate (UUID requestID, OSHttpRequest request)
                {
                    APollRequest reqinfo = new()
                    {
                        thepoll = this,
                        reqID = requestID,
                        request = request
                    };

                    m_workerpool.Enqueue(reqinfo);
                    return null;
                };

                // this should never happen except possible on shutdown
                NoEvents = delegate (UUID _, UUID _)
                {
                    /*
                    lock (requests)
                    {
                        Hashtable request = requests.Find(id => id["RequestID"].ToString() == x.ToString());
                        requests.Remove(request);
                    }
                    */
                    Hashtable response = new Hashtable();

                    response["int_response_code"] = 500;
                    response["str_response_string"] = "timeout";
                    response["content_type"] = "text/plain";
                    response["keepalive"] = false;
                    return response;
                };
            }

            public void Process(APollRequest requestinfo)
            {
                UUID requestID = requestinfo.reqID;

                if(m_scene.ShuttingDown)
                    return;

                lock(responses)
                {
                    lock(dropedResponses)
                    {
                        if(dropedResponses.Contains(requestID))
                        {
                            dropedResponses.Remove(requestID);
                            return;
                        }
                    }
/* can't do this with current viewers; HG problem
                    // If the avatar is gone, don't bother to get the texture
                    if(m_scene.GetScenePresence(Id) == null)
                    {
                        curresponse = new Hashtable();
                        curresponse["int_response_code"] = 500;
                        curresponse["str_response_string"] = "timeout";
                        curresponse["content_type"] = "text/plain";
                        curresponse["keepalive"] = false;
                        responses[requestID] = new APollResponse() { bytes = 0, response = curresponse };
                        return;
                    }
*/
                }
                OSHttpResponse response = new OSHttpResponse(requestinfo.request);
                m_getAssetHandler.Handle(requestinfo.request, response, m_hgassets);

                lock(responses)
                {
                    lock(dropedResponses)
                    {
                        if(dropedResponses.Contains(requestID))
                        {
                            dropedResponses.Remove(requestID);
                            return;
                        }
                    }

                    APollResponse preq= new APollResponse()
                    {
                        osresponse = response
                    };
                    responses[requestID] = preq;
                }
            }
        }

        public void RegisterCaps(UUID agentID, Caps caps)
        {
            /*
            string hostName = m_scene.RegionInfo.ExternalHostName;
            uint port = (MainServer.Instance == null) ? 0 : MainServer.Instance.Port;
            string protocol = "http";
            if (MainServer.Instance.UseSSL)
            {
                hostName = MainServer.Instance.SSLCommonName;
                port = MainServer.Instance.SSLPort;
                protocol = "https";
            }
            string baseURL = String.Format("{0}://{1}:{2}", protocol, hostName, port);
            */
            string hgassets = null;
            if(m_UserManagement != null)
                hgassets = m_UserManagement.GetUserServerURL(agentID, "AssetServerURI");

            IExternalCapsModule handler = m_scene.RequestModuleInterface<IExternalCapsModule>();

            // one budget per agent, shared by all its asset caps in all regions of this instance
            AgentAssetBudget budget = AcquireBudget(agentID);

            if (m_GetTextureURL.Equals("localhost"))
            {
                string capUrl = "/" + UUID.Random();

                if (handler != null)
                    handler.RegisterExternalUserCapsHandler(agentID, caps, "GetTexture", capUrl);
                else
                    caps.RegisterPollHandler("GetTexture", new PollServiceAssetEventArgs(capUrl, agentID, m_scene, hgassets, budget));
            }
            else
            {
                caps.RegisterHandler("GetTexture", m_GetTextureURL);
            }

            //GetMesh
            if (m_GetMeshURL.Equals("localhost"))
            {
                string capUrl = "/" + UUID.Random();

                if (handler != null)
                    handler.RegisterExternalUserCapsHandler(agentID, caps, "GetMesh", capUrl);
                else
                    caps.RegisterPollHandler("GetMesh", new PollServiceAssetEventArgs(capUrl, agentID, m_scene, hgassets, budget));
            }
            else if (!string.IsNullOrEmpty(m_GetMeshURL))
                caps.RegisterHandler("GetMesh", m_GetMeshURL);

            //GetMesh2
            if (m_GetMesh2URL.Equals("localhost"))
            {
                string capUrl = "/" + UUID.Random();

                if (handler != null)
                    handler.RegisterExternalUserCapsHandler(agentID, caps, "GetMesh2", capUrl);
                else
                    caps.RegisterPollHandler("GetMesh2", new PollServiceAssetEventArgs(capUrl, agentID, m_scene, hgassets, budget));
            }
            else if (!string.IsNullOrEmpty(m_GetMesh2URL))
                caps.RegisterHandler("GetMesh2", m_GetMesh2URL);

            //ViewerAsset
            if (m_GetAssetURL.Equals("localhost"))
            {
                string capUrl = "/" + UUID.Random();

                if (handler != null)
                    handler.RegisterExternalUserCapsHandler(agentID, caps, "ViewerAsset", capUrl);
                else
                    caps.RegisterPollHandler("ViewerAsset", new PollServiceAssetEventArgs(capUrl, agentID, m_scene, hgassets, budget));
            }
            else if (!string.IsNullOrEmpty(m_GetAssetURL))
                caps.RegisterHandler("ViewerAsset", m_GetAssetURL);
        }
    }
}
