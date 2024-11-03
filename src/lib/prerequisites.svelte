<script lang='ts'>
    import { onMount } from 'svelte';
    import type BasicProps from './basicProps';
    import Prerequisite from './prerequisite.svelte';
    import { invoke } from '@tauri-apps/api/core';

    let {
        canAdvance = $bindable(),
    }: BasicProps = $props();

    let prereqs: [string, boolean | undefined][] = $state([]);

    onMount(() => {
        restart();
    });

    function restart() {
        prereqs = [];
        canAdvance = false;
        start();
    }

    async function start() {
        prereqs = [
            ['Checking if FINAL FANTASY XIV is running', undefined],
            ['Checking if XIVLauncher is installed', undefined],
            ['Checking if XIVLauncher has been opened', undefined],
        ];

        await Promise.allSettled([
            checkFfxiv(prereqs, 0),
            checkXlInstalled(prereqs, 1),
            checkXlOpened(prereqs, 2),
        ]);

        const allSatisfied = prereqs
            .map(([_, status]) => status)
            .every(status => status === true);
        canAdvance = allSatisfied;
    }

    async function checkFfxiv(reqs: typeof prereqs, idx: number) {
        const running = await invoke<boolean>('check_for_process', { name: 'ffxiv_dx11' });
        reqs[idx] = [
            running
                ? 'FINAL FANTASY XIV must be closed for the installer to continue'
                : 'FINAL FANTASY XIV is closed',
            !running,
        ];
    }

    async function checkXlInstalled(reqs: typeof prereqs, idx: number) {
        const installed = await invoke<boolean>('initialise');
        reqs[idx] = [
            installed
                ? 'XIVLauncher is installed'
                : '<a href="https://goatcorp.github.io/">XIVLauncher</a> must be installed before continuing',
            installed,
        ];
    }

    async function checkXlOpened(reqs: typeof prereqs, idx: number) {
        const opened = await invoke<boolean>('dalamud_config_present');
        reqs[idx] = [
            opened
                ? 'XIVLauncher has been opened at least once'
                : 'XIVLauncher must be launched at least once before continuing',
            opened,
        ];
    }
</script>

<h1>Checking prerequisites</h1>

<ul>
    {#each prereqs as [label, state]}
        {#snippet labelSnippet()}
            {@html label}
        {/snippet}

        <Prerequisite
            label={labelSnippet}
            {state}
        />
    {/each}
</ul>

{#if canAdvance}
    <strong>You're all set. Click next to begin the installation.</strong>
{:else}
    <button
        class='fullwidth'
        onclick={restart}
    >
        Check again
    </button>
{/if}
