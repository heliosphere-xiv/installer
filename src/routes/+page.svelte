<script lang="ts">
    import Installer from '$lib/installer.svelte';
    import Prerequisites from '$lib/prerequisites.svelte';
    import Splash from '$lib/splash.svelte';

    let step = $state(0);
    let canAdvance = $state(true);

    function advanceStep() {
        step += 1;
    }
</script>

<main class='container'>
    <div class='step'>
        {#if step === 0}
            <Splash
                bind:canAdvance={canAdvance}
                onnext={advanceStep}
            />
        {:else if step === 1}
            <Prerequisites
                bind:canAdvance={canAdvance}
            />
        {:else if step === 2}
            <Installer
                bind:canAdvance={canAdvance}
            />
        {/if}
    </div>

    {#if step !== 0}
        <div class='step-buttons'>
            <button
                disabled={step === 0}
                class='secondary'
                onclick={() => step -= 1}
            >
                Back
            </button>
            <button
                disabled={!canAdvance}
                onclick={advanceStep}
            >
                Next
            </button>
        </div>
    {/if}

</main>

<style lang='scss'>
    .step-buttons {
        display: flex;
        justify-content: space-between;
        align-items: center;
        width: 100%;
    }

    .container {
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: space-between;
        height: 100vh;

        & > .step {
            flex-grow: 1;
        }
    }
</style>
