// Narrow, bounded IPC bridge to maintained libtorrent 2.x. No torrent handles,
// payloads, sample_infohashes, announces, database, or second DHT implementation.
#include <libtorrent/session.hpp>
#include <libtorrent/session_params.hpp>
#include <libtorrent/alert_types.hpp>
#include <libtorrent/bencode.hpp>
#include <libtorrent/kademlia/dht_state.hpp>
#include <libtorrent/version.hpp>
#include <nlohmann/json.hpp>
#include <iostream>
#include <map>
#include <memory>
#include <chrono>

namespace lt = libtorrent;
using json = nlohmann::json;
using clock_type = std::chrono::steady_clock;
constexpr int frame_limit = 262144;

std::string hex(lt::span<char const> s) {
    static char const* digits = "0123456789abcdef";
    std::string out;
    for (unsigned char c : s) { out += digits[c >> 4]; out += digits[c & 15]; }
    return out;
}
std::string unhex(std::string const& s) {
    if (s.size() % 2) throw std::runtime_error("invalid hex");
    std::string out;
    auto nibble = [](char c) {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        throw std::runtime_error("invalid hex");
    };
    for (size_t i = 0; i < s.size(); i += 2) out += char(nibble(s[i])*16 + nibble(s[i+1]));
    return out;
}
json read_frame() {
    // getline(string) would allocate before enforcing the bound.
    char buf[frame_limit + 1];
    if (!std::cin.getline(buf, sizeof(buf))) throw std::runtime_error("IPC EOF/oversize");
    return json::parse(buf);
}
void send(json const& j) {
    auto s = j.dump();
    if (s.size() >= frame_limit) throw std::runtime_error("IPC output oversize");
    std::cout << s << std::endl;
}

int main() try {
    auto cfg = read_frame();
    int const max_queries = cfg.at("max_queries");
    int const max_nodes = cfg.at("max_nodes");
    if (max_queries < 1 || max_queries > 16 || max_nodes < 1 || max_nodes > 256)
        throw std::runtime_error("invalid native bounds");
    lt::session_params params;
    bool recovered = false;
    auto state = unhex(cfg.value("state", std::string()));
    if (!state.empty()) {
        lt::error_code ec;
        auto node = lt::bdecode(lt::span<char const>(state), ec, nullptr, 10, 10000);
        if (ec || node.type() != lt::bdecode_node::dict_t) recovered = true;
        else {
            params = lt::read_session_params(node, lt::session_handle::save_dht_state);
            if (params.dht_state.nids.size() != 1 || params.dht_state.nodes.size() > size_t(max_nodes)
                || !params.dht_state.nodes6.empty()) {
                params.dht_state.clear(); recovered = true;
            }
        }
    }
    // Exactly one configured IPv4 interface. Associate the saved identity with
    // this interface even when an orchestrator assigned a new private pod IP.
    if (!params.dht_state.nids.empty())
        params.dht_state.nids.front().first = lt::make_address(cfg.at("bind_ip").get<std::string>());
    auto& s = params.settings;
    s.set_str(lt::settings_pack::listen_interfaces, cfg.at("listen").get<std::string>());
    s.set_str(lt::settings_pack::dht_bootstrap_nodes, cfg.at("bootstrap").get<std::string>());
    // Enable once AFTER initial settings have been applied. Starting with DHT
    // enabled and an empty bootstrap list can restart it during initialization,
    // consuming (then discarding) the restored routing state in libtorrent 2.0.
    s.set_bool(lt::settings_pack::enable_dht, false);
    s.set_bool(lt::settings_pack::enable_lsd, false);
    s.set_bool(lt::settings_pack::enable_upnp, false);
    s.set_bool(lt::settings_pack::enable_natpmp, false);
    s.set_bool(lt::settings_pack::enable_incoming_tcp, false);
    s.set_bool(lt::settings_pack::enable_outgoing_tcp, false);
    s.set_bool(lt::settings_pack::enable_incoming_utp, false);
    s.set_bool(lt::settings_pack::enable_outgoing_utp, false);
    s.set_bool(lt::settings_pack::dht_read_only, true); // no inbound peer/item storage
    s.set_bool(lt::settings_pack::dht_extended_routing_table, false);
    s.set_bool(lt::settings_pack::dht_aggressive_lookups, false);
    bool const local = cfg.at("allow_private");
    s.set_bool(lt::settings_pack::dht_ignore_dark_internet, !local);
    s.set_bool(lt::settings_pack::dht_restrict_routing_ips, !local);
    s.set_bool(lt::settings_pack::dht_restrict_search_ips, !local);
    s.set_int(lt::settings_pack::dht_search_branching, 2);
    s.set_int(lt::settings_pack::dht_max_torrents, 1);
    s.set_int(lt::settings_pack::dht_max_peers, 1);
    s.set_int(lt::settings_pack::dht_max_dht_items, 1);
    s.set_int(lt::settings_pack::dht_upload_rate_limit, cfg.at("upload_rate"));
    s.set_int(lt::settings_pack::alert_queue_size, 256);
    s.set_int(lt::settings_pack::alert_mask, int(lt::alert_category::dht | lt::alert_category::error));
    lt::session session(std::move(params));
    // session_state is a synchronization barrier for initial settings callbacks.
    session.session_state(lt::session_handle::save_dht_state);
    lt::settings_pack enable;
    enable.set_bool(lt::settings_pack::enable_dht, true);
    session.apply_settings(enable);
    struct query { int id; };
    std::map<int, std::unique_ptr<query>> pending;
    json nodes = json::array();
    std::string node_id;
    int routing_nodes = 0;
    auto next_nodes = clock_type::now();
    send({{"ready", true}, {"recovered", recovered}, {"libtorrent", LIBTORRENT_VERSION}});
    for (;;) {
        auto cmd = read_frame();
        json out = {{"events", json::array()}};
        auto op = cmd.at("op").get<std::string>();
        if (op == "query") {
            int id = cmd.at("id");
            if (pending.size() >= size_t(max_queries) || pending.count(id)) out["accepted"] = false;
            else {
                auto raw = unhex(cmd.at("hash"));
                if (raw.size() != 20) throw std::runtime_error("invalid hash");
                auto ep = lt::udp::endpoint(lt::make_address(cmd.at("ip").get<std::string>()), cmd.at("port").get<unsigned short>());
                if (!ep.address().is_v4() || ep.port() == 0) throw std::runtime_error("invalid endpoint");
                lt::entry request;
                request["y"] = "q";
                request["q"] = "get_peers";
                request["a"]["info_hash"] = raw;
                request["a"]["scrape"] = 1;
                request["a"]["want"].list().emplace_back("n4");
                auto q = std::make_unique<query>(query{id});
                session.dht_direct_request(ep, request, lt::client_data_t(q.get()));
                pending.emplace(id, std::move(q));
                out["accepted"] = true;
            }
        } else if (op == "state") {
            auto saved = session.session_state(lt::session_handle::save_dht_state);
            if (saved.dht_state.nodes.size() > size_t(max_nodes)) saved.dht_state.nodes.resize(max_nodes);
            saved.dht_state.nodes6.clear();
            auto bytes = lt::write_session_params_buf(saved, lt::session_handle::save_dht_state);
            if (saved.dht_state.nids.size() == 1) out["state"] = hex(bytes);
        } else if (op == "close") { send({{"closed", true}}); break; }
        else if (op != "poll") throw std::runtime_error("invalid IPC op");
        if (clock_type::now() >= next_nodes) {
            auto ids = session.session_state(lt::session_handle::save_dht_state).dht_state.nids;
            if (ids.size() > 1) throw std::runtime_error("more than one DHT node");
            if (!ids.empty()) session.dht_live_nodes(ids.front().second);
            next_nodes = clock_type::now() + std::chrono::seconds(1);
        }
        std::vector<lt::alert*> alerts;
        session.pop_alerts(&alerts);
        for (auto* alert : alerts) {
            if (auto* a = lt::alert_cast<lt::dht_live_nodes_alert>(alert)) {
                nodes = json::array();
                node_id = hex({a->node_id.data(), 20});
                routing_nodes = a->num_nodes();
                for (auto const& n : a->nodes()) {
                    if (nodes.size() >= size_t(max_nodes)) break;
                    if (n.second.address().is_v4()) nodes.push_back({hex({n.first.data(),20}), n.second.address().to_string(), n.second.port()});
                }
            } else if (auto* a = lt::alert_cast<lt::dht_direct_response_alert>(alert)) {
                auto* q = a->userdata.get<query>();
                if (!q || !pending.count(q->id)) continue;
                json event = {{"id", q->id}, {"valid", false}};
                auto response = a->response();
                if (response && response.data_section().size() <= 4096) {
                    auto r = response.dict_find_dict("r");
                    if (r && r.dict_find_string_value("id").size() == 20) {
                        event["valid"] = true;
                        for (auto key : {"BFsd", "BFpe", "nodes"}) {
                            auto value = r.dict_find_string_value(key);
                            if (value.size() <= 4096) event[key] = hex({value.data(), static_cast<std::ptrdiff_t>(value.size())});
                        }
                        event["values"] = json::array();
                        auto values = r.dict_find_list("values");
                        if (values) for (int i = 0; i < values.list_size() && i < 256; ++i) {
                            auto value = values.list_string_value_at(i);
                            if (value.size() == 6 || value.size() == 18)
                                event["values"].push_back(hex({value.data(), static_cast<std::ptrdiff_t>(value.size())}));
                        }
                        event["truncated"] = values && values.list_size() > 256;
                    }
                }
                out["events"].push_back(event);
                pending.erase(q->id);
            }
        }
        out["nodes"] = nodes;
        out["node_id"] = node_id;
        out["routing_nodes"] = routing_nodes;
        out["pending"] = pending.size();
        send(out);
    }
    return 0;
} catch (std::exception const& e) {
    std::cerr << "dht bridge: " << e.what() << '\n';
    return 1;
}
