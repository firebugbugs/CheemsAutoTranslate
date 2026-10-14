package com.cheems.translator.mobile.data

import android.net.Uri
import java.net.InetAddress

object EndpointValidator {
    fun errorFor(endpoint: String): String? {
        val uri = runCatching { Uri.parse(endpoint.trim()) }.getOrNull()
            ?: return "请填写有效的接口地址。"
        val scheme = uri.scheme?.lowercase()
        val host = uri.host?.lowercase()?.trimEnd('.')
        if (host.isNullOrBlank() || uri.userInfo != null || uri.query != null || uri.fragment != null) {
            return "接口地址需要包含有效主机名，且不能包含账号、查询参数或片段。"
        }
        if (scheme == "https") return null
        if (scheme != "http") return "接口地址需要使用 HTTPS；HTTP 仅支持本机或局域网地址。"
        if (!isPrivateHost(host)) return "明文 HTTP 仅允许本机或私有局域网地址；公网接口请使用 HTTPS。"
        return null
    }

    private fun isPrivateHost(host: String): Boolean {
        if (host == "localhost" || host.endsWith(".localhost") || host.endsWith(".local")) return true
        val parts = host.split('.')
        if (parts.size == 4) {
            val octets = parts.map { it.toIntOrNull() ?: return false }
            if (octets.any { it !in 0..255 }) return false
            return octets[0] == 10
                || octets[0] == 127
                || (octets[0] == 192 && octets[1] == 168)
                || (octets[0] == 172 && octets[1] in 16..31)
                || (octets[0] == 169 && octets[1] == 254)
        }
        if (!host.contains(':')) return false
        val address = runCatching { InetAddress.getByName(host) }.getOrNull() ?: return false
        if (address.isLoopbackAddress || address.isLinkLocalAddress) return true
        val firstOctet = address.address.firstOrNull()?.toInt()?.and(0xff) ?: return false
        return firstOctet and 0xfe == 0xfc
    }
}
